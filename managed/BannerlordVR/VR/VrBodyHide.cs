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
    /// NOTHING IS STASHED, ONLY A KNOWN-GOOD VALUE PUT BACK
    ///
    /// VrAgentVisibility has the scar for this one: the previous attempt at
    /// meddling with agent rendering held engine pointers across the agent's
    /// lifetime and handed one back after the engine had freed it. A MetaMesh
    /// belongs to an agent that can die between the hide and the restore, so a
    /// mesh's ORIGINAL mask is never cached for later - a re-show sets every
    /// kept mesh to VisibilityMaskFlags.DefaultDynamic instead, the value every
    /// skinned character mesh here already carries before anything hides it, so
    /// there is nothing agent-specific to outlive. That is also what makes a
    /// live toggle - hide_body off, or hands_only on, mid-mission rather than at
    /// the next one - take effect on the very next Tick instead of only on a
    /// fresh entity.
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

        /// <summary>
        /// Whether the hand meshes stay drawn alongside the weapon while the
        /// rest of the body is hidden. Only has an effect while <see
        /// cref="Hiding"/> is on - this is a variant of the full hide, not the
        /// shown-body mode, so toggling it off HideBody does nothing.
        ///
        /// Matched by name against the same mesh table hide_body_log prints,
        /// exactly like <see cref="HeadNames"/> does for the shown-body branch:
        /// "hands_male_a" is what the log shows for the bare-handed player, and
        /// a glove or gauntlet equipped over it is its own mesh with its own
        /// name, hence the wider default list.
        /// </summary>
        public static bool HandsOnly { get; private set; } = VrConfig.Bool("hands_only", false);

        /// <summary>Set from the VR panel. Takes effect on the next visibility
        /// pass, like <see cref="SetHiding"/>.</summary>
        public static void SetHandsOnly(bool handsOnly)
        {
            if (HandsOnly == handsOnly)
                return;

            HandsOnly = handsOnly;
            Reset();
        }

        private static readonly string[] HandNames = Names(VrConfig.String(
            "hide_body_hand_meshes", "hands_,glove,gauntlet"));

        /// <summary>A hand or glove mesh by name (hide_body_hand_meshes). TEST 480's
        /// finger copies are named "bvr_fingers_..." and never count.</summary>
        internal static bool IsHandName(string name)
        {
            string lower = (name ?? string.Empty).ToLowerInvariant();
            if (lower.Length == 0 || lower.StartsWith("bvr_fingers_"))
                return false;

            foreach (string hand in HandNames)
            {
                if (lower.Contains(hand))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether to hide just the head and neck meshes while the body IS shown.
        ///
        /// WHY THE HEAD HAS TO GO. The eye camera sits inside the skull. At the
        /// near plane - five centimetres, deliberately close so your own hands do
        /// not clip - the inside of a head mesh is either a black screen or a ring
        /// of inverted faces, depending on which way its normals point. Nothing
        /// else about a drawn body is in the way.
        ///
        /// The names are matched as substrings against the mesh table this class
        /// prints (hide_body_log), and the list is editable because what a body
        /// mesh is called is game data rather than a fact: if the head turns out
        /// to be part of one big body mesh, this list will match nothing and the
        /// log will say so - at which point hide_body = 1 is the honest fallback.
        /// </summary>
        private static readonly bool HideHead = VrConfig.Bool("hide_head", true);

        private static readonly string[] HeadNames = Names(VrConfig.String(
            "hide_head_meshes", "head,hair,beard,moustache,brow,teeth,tongue,helmet,hood,hat"));

        private static string[] Names(string list)
        {
            if (string.IsNullOrEmpty(list))
                return new string[0];

            string[] parts = list.Split(',');
            var cleaned = new System.Collections.Generic.List<string>(parts.Length);

            foreach (string part in parts)
            {
                string name = part.Trim().ToLowerInvariant();
                if (name.Length > 0)
                    cleaned.Add(name);
            }

            return cleaned.ToArray();
        }

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
            if ((!Hiding && !HideHead) || _broken || agent == null || entity == null)
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

                // The full hide only when the weapons are on their own entities:
                // hiding the body's meshes would take a weapon that shares the
                // entity with it. The head list is matched by name and never
                // touches a "wpn_" mesh, so it needs no such check.
                if (Hiding && !WeaponsAreSeparate(agent, entity))
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
                if (mesh == null || VrHandMesh.Owns(mesh.GetName()))
                    continue;

                if (Hides(mesh.GetName()))
                {
                    // Nought rather than "clear the Final bit": the shadow bits
                    // would otherwise leave the body casting a shadow it no longer
                    // has, and a headless shadow at first-person range is more
                    // startling than the body was.
                    mesh.SetVisibilityMask(0);
                    hidden++;
                }
                else
                {
                    // Restored rather than left alone: hands_only and hide_head
                    // can flip a mesh from hidden to kept on the SAME entity - the
                    // panel toggling mid-mission, not a fresh one - and a mesh this
                    // pass zeroed earlier stays zero forever without this. Every
                    // skinned character mesh this class hides is dynamic, so this
                    // is also what a never-hidden mesh already carries - setting it
                    // again is a no-op for those.
                    mesh.SetVisibilityMask(VisibilityMaskFlags.DefaultDynamic);
                    kept++;
                }
            }

            if (skeleton != null)
            {
                foreach (Mesh mesh in skeleton.GetAllMeshes())
                {
                    // TEST 481: our finger copies, and the game's hands while
                    // they are drawn, are VrHandMesh's to show and hide.
                    if (mesh == null || VrHandMesh.Owns(mesh.Name))
                        continue;

                    if (Hides(mesh.Name))
                    {
                        mesh.SetVisibilityMask(0);
                        hidden++;
                    }
                    else
                    {
                        mesh.SetVisibilityMask(VisibilityMaskFlags.DefaultDynamic);
                        kept++;
                    }
                }
            }

            if (_loggedResult)
                return;

            _loggedResult = true;

            if (Hiding)
            {
                VrLog.Info("Body hidden: " + hidden + " mesh(es) taken out of the render, "
                           + kept + " kept. The weapon and shield are separate entities with "
                           + "their own meshes, so they carry on drawing"
                           + (HandsOnly
                                ? "; hands_only kept the hand mesh too ('"
                                  + string.Join(",", HandNames) + "' - correct it from the "
                                  + "mesh table above if the hand didn't match)."
                                : ".")
                           + " Set hide_body = 0 to get the body back (hide_head then decides "
                           + "whether the head stays).");
            }
            else if (hidden == 0)
            {
                VrLog.Warn("Body shown, but NO mesh matched the head list ('"
                           + string.Join(",", HeadNames) + "') - nothing was hidden. The mesh "
                           + "table above names every one of them. Either add the head's own "
                           + "name to hide_head_meshes, or the head is part of one body mesh "
                           + "and hide_body = 1 is the honest answer.");
            }
            else
            {
                VrLog.Info("Body shown, head hidden: " + hidden + " mesh(es) matching '"
                           + string.Join(",", HeadNames) + "' taken out of the render, " + kept
                           + " kept. If the head is still in the way, the mesh table above "
                           + "names it - add it to hide_head_meshes, or set hide_head = 0 and "
                           + "hide_body = 1 to go back to a hidden body.");
            }
        }

        /// <summary>Whether this mesh comes out of the render this pass: everything
        /// but the weapon meshes when the body is hidden, and only the head-named
        /// ones when it is not.</summary>
        private static bool Hides(string name)
        {
            if (Hiding)
                return !Skip(name);

            string lower = (name ?? string.Empty).ToLowerInvariant();
            if (lower.Length == 0)
                return false;

            for (int i = 0; i < HeadNames.Length; i++)
            {
                if (lower.Contains(HeadNames[i]))
                    return true;
            }

            return false;
        }

        private static bool Skip(string name)
        {
            string lower = (name ?? string.Empty).ToLowerInvariant();
            if (lower.Length == 0)
                return false;

            if (Keep.Length > 0 && lower.Contains(Keep))
                return true;

            if (HandsOnly)
            {
                for (int i = 0; i < HandNames.Length; i++)
                {
                    if (lower.Contains(HandNames[i]))
                        return true;
                }
            }

            return false;
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
                        line.Append("   (kept - matches hide_body_keep or hide_body_hand_meshes)");

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
