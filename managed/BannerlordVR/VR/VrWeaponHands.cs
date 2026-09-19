using System;
using System.Text;
using TaleWorlds.Core;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Puts each hand where its controller is.
    ///
    /// HOW A WEAPON IS MOVED AT ALL
    ///
    /// The weapon is not an independent object that can be repositioned. It is
    /// parented to a bone under the hand - r_finger0 for a weapon, l_finger0 for a
    /// shield, per the game's own monsters.xml - so the only way to move it is to
    /// move the hand, after the engine has finished animating it, every frame.
    ///
    /// Skeleton.SetBoneLocalFrame takes a frame relative to the bone's PARENT, so
    /// the target has to be walked down into that space:
    ///
    ///     world  ->  agent entity  ->  parent bone  ->  local
    ///
    /// Each step is one TransformToLocal. The parent's entitial frame is read back
    /// from the skeleton in the same frame it was animated, so the hand lands where
    /// the controller is regardless of what the animation was doing.
    ///
    /// BOTH HANDS, AND WHICH CONTROLLER DRIVES WHICH
    ///
    /// This used to pose one bone, named in the config. That cannot express "the
    /// shield goes on the left and the sword on the right and the bow on the left
    /// even though the game keeps it in the main hand", so the decision moved out
    /// to VrHandSlots, which answers it from the equipment. This class no longer
    /// has an opinion about hands - it takes two slots and poses them.
    ///
    /// THE ARM, AND WHY IT IS NO LONGER THE PROBLEM
    ///
    /// Only the hand bones are posed, so the forearm, upper arm and clavicle stay
    /// wherever the animation put them. The arm therefore runs from a shoulder
    /// doing one thing to a hand doing another, and at first-person range that
    /// disagreement is most of what you can see.
    ///
    /// It was in the right place and it still looked wrong, which is worth being
    /// precise about: the weapon was tracking correctly the whole time and the
    /// body drawn around it was not.
    ///
    /// Solving the arm means two-bone IK - the elbow plane, a cross-body rule, and
    /// an answer for a target that is out of reach. VrBodyHide takes the other
    /// road and removes the body, which deletes the question rather than answering
    /// it, and leaves a floating weapon that tracks your hand. That is a complete
    /// thing in its own right and what a good many VR games ship on purpose.
    ///
    /// The IK is still worth doing later, and the bone table this class logs is
    /// what makes it something other than guesswork when it happens.
    /// </summary>
    public static class VrWeaponHands
    {
        public static bool Enabled { get; } = VrConfig.Bool("weapon_follow", true);

        /// <summary>Whether to pose hands and arms while mounted. On since TEST 206
        /// found what was actually dropping the rider and RestoreRoot put it back.
        /// Off leaves the rider entirely to the game.</summary>
        private static readonly bool PoseWhileMounted =
            VrConfig.Bool("motion_hands_mounted", true);

        /// <summary>Whether to put the engine's own root-bone placement back
        /// before rebuilding the entitial frames. On, because without it a rider
        /// drops onto the horse's ground position - TEST 206. Off reproduces the
        /// collapse.</summary>
        private static readonly bool RestoreRoot = VrConfig.Bool("root_lift_restore", true);

        /// <summary>TEST 206. Measures a rider instead of predicting one: whether
        /// the two routes into world space agree, and whether rebuilding the
        /// entitial frames is what drops the body. Writes no bone. See
        /// MountedProbe.</summary>
        private static readonly bool Probe = VrConfig.Bool("mounted_probe", false);

        private static int _probeTick;

        // The root bone, found once per mission. See RootBone.
        private static sbyte _root = -1;
        private static bool _haveRoot;

        private static bool _loggedMounted;

        private static bool _broken;
        private static bool _loggedBones;
        private static bool _loggedDriving;

        // Bones written this frame and the entitial frame each was aimed at, held
        // until the entitial frames have been recomputed. Two hands, so two slots.
        private static readonly sbyte[] _pendingBone = new sbyte[2];
        private static readonly MatrixFrame[] _pendingWanted = new MatrixFrame[2];
        private static readonly MatrixFrame[] _pendingWorld = new MatrixFrame[2];
        private static readonly sbyte[] _pendingGripBone = new sbyte[2];
        private static int _pendingCount;

        private static int _alignTick;

        public static void Tick()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                Agent agent = Mission.Current?.MainAgent;
                if (agent == null || !agent.IsActive() || !VrCameraDriver.HasPose)
                    return;

                MBAgentVisuals visuals = agent.AgentVisuals;
                if (visuals == null)
                    return;

                Skeleton skeleton = visuals.GetSkeleton();
                GameEntity entity = visuals.GetEntity();
                if (skeleton == null || entity == null)
                    return;

                if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                    return;

                DescribeSkeletonOnce(skeleton);

                // Before anything is written this frame, so it sees the bones as
                // the animation left them. See VrArmSeam for what it is asking.
                VrArmSeam.BeforeWrites(skeleton);
                _pendingCount = 0;

                // THE ROOT'S PLACEMENT, CAPTURED BEFORE IT CAN BE LOST.
                //
                // TEST 206 measured this rather than arguing about it. On a horse
                // the engine places the rider by moving the ROOT bone, and that
                // placement does not live in a local frame:
                //
                //   rebuild with nothing written at all:
                //     root z 1.800 -> 0.915    head bone z 2.449 -> 1.564
                //
                // The root and the head fall by the SAME amount in every sample
                // (0.885, 0.885), and the after-value is the same 0.915 every
                // frame while the before-value breathes with the animation. So it
                // is one rigid translation carried on the root, recomputed per
                // frame by the engine, and UpdateEntitialFramesFromLocalFrames
                // below rebuilds the root from its static local frame and drops
                // the whole rider onto the horse's ground position.
                //
                // Because it is rigid and on the root, putting the root back puts
                // everything back - every other bone composes down from it. The
                // root is parentless, so its local frame IS its entitial frame and
                // the restore is a single write before the rebuild rather than a
                // second rebuild after it.
                //
                // Not gated on being mounted. A no-op on foot, where the two
                // values already agree, and correct anywhere else the engine
                // decides to place the root itself.
                sbyte root = RootBone(skeleton);
                MatrixFrame rootPlaced = root >= 0
                    ? skeleton.GetBoneEntitialFrameWithIndex(root)
                    : MatrixFrame.Identity;

                // The body goes away here rather than in its own tick, because this
                // is where the agent, the visuals and the entity have all already
                // been resolved and checked.
                VrBodyHide.Tick(agent, visuals, entity, skeleton);

                // MOUNTED IS POSED LIKE ANYTHING ELSE NOW.
                //
                // It was not, for a long time, on the reasoning that posing bones
                // on a rider collapses the body onto the horse's ground position.
                // That much was true and the collapse was real. The half that was
                // missing was WHY, and it mattered, because the two candidates
                // wanted opposite fixes.
                //
                // TEST 206 settled it by measuring both at once:
                //
                //   entity.GetGlobalFrame() vs visuals.GetFrame():  0.000 m apart
                //   rebuild with nothing written:  root z 1.800 -> 0.915
                //                                  head z 2.449 -> 1.564
                //
                // So the conversion into entitial space was never wrong - the two
                // routes are the same frame - and the rebuild really was eating the
                // rider's placement. Root and head fall by an identical 0.885 m,
                // which is what says it is one rigid translation on the root rather
                // than something spread through the pose.
                //
                // RestoreRoot above puts that translation back, so there is nothing
                // left for a mounted special case to do. The probe stays behind
                // mounted_probe for whenever this needs measuring again.
                if (agent.HasMount)
                {
                    // Runs whether or not the posing does. The whole point of TEST
                    // 206 is to measure a rider this mod is NOT touching, so the
                    // probe has to sit on the near side of the early return.
                    MountedProbe(agent, visuals, entity, skeleton, input);

                    if (!PoseWhileMounted)
                    {
                        MountedOnce();
                        return;
                    }
                }

                if (!VrHandSlots.Resolve(agent, skeleton, out VrHandSlot main, out VrHandSlot off))
                    return;

                MatrixFrame agentGlobal = entity.GetGlobalFrame();

                // The automatic pass is started HERE rather than on a timer, on the
                // first frame there is actually something to watch. Begun any
                // earlier it would spend its whole window looking at a loading
                // screen and then honestly report that it saw nothing.
                if (VrHandCalibration.WantsAuto && Tracking(input, main, off))
                    VrHandCalibration.StartAuto(persist: false, onlyMissing: true);

                // Each hand is either being WATCHED or being POSED, never both.
                //
                // A hand that is sampling is deliberately left on its animation, so
                // that what gets read back is the animation's own orientation - the
                // one the weapon mesh is bound to, and therefore correct by
                // construction. A hand that is posed follows its controller.
                //
                // Per hand rather than all-or-nothing: a calibrated hand keeps
                // tracking while the other one samples, which is what makes a
                // mission-start pass on one hand invisible instead of a two-second
                // freeze of both.
                bool posed = Drive(agent, visuals, input, skeleton, agentGlobal, main);
                posed |= Drive(agent, visuals, input, skeleton, agentGlobal, off);

                if (!posed)
                    return;

                // Put the engine's own root placement back first, so the rebuild
                // composes every bone down from where the rider actually is
                // instead of from the root's static rest height. See the capture
                // above and TEST 206.
                if (RestoreRoot && root >= 0)
                    skeleton.SetBoneLocalFrame(root, rootPlaced);

                // Once, after both hands are set. The entitial frames are what the
                // renderer and the attached meshes read, and recomputing them per
                // hand would do the same walk down the skeleton twice.
                skeleton.UpdateEntitialFramesFromLocalFrames();

                // Only now do the entitial frames reflect this frame's writes, so
                // this is the one point where "did it land" is a real question.
                for (int i = 0; i < _pendingCount; i++)
                    VrArmSeam.AfterWrite(skeleton, _pendingBone[i], _pendingWanted[i]);

                TraceAlignment(agent, skeleton, agentGlobal);

                if (!_loggedDriving)
                {
                    _loggedDriving = true;
                    VrLog.Info(VrBodyHide.Enabled
                        ? "Hands are following the controllers, turned the way the animation "
                          + "holds them. The body is hidden, so what you see is the weapon and "
                          + "the shield alone - no arms to disagree with them. Press the "
                          + "calibration key to adjust a hand by eye."
                        : "Hands are following the controllers, turned the way the animation "
                          + "holds them and positioned where the controllers are. The ARMS will "
                          + "look wrong - only the hand bones are posed, so the forearms point "
                          + "wherever the animation left them. hide_body = 1 removes the "
                          + "argument entirely.");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Hand posing failed; the arms go back to the game's own animation. "
                            + "Nothing else is affected.", ex);
            }
        }


        /// <summary>
        /// The skeleton's root: the one bone with no parent.
        ///
        /// Searched rather than assumed to be index 0. It always is in practice,
        /// but the whole restore hangs on this bone being parentless - that is what
        /// makes its local frame equal its entitial frame and the restore a single
        /// write - so it is worth establishing rather than hoping. A skeleton with
        /// no parentless bone returns -1 and the restore simply does not run.
        ///
        /// Cached per mission; a skeleton's hierarchy does not change under us, and
        /// this would otherwise walk the bone table every frame.
        /// </summary>
        private static sbyte RootBone(Skeleton skeleton)
        {
            if (_haveRoot)
                return _root;

            _haveRoot = true;
            _root = -1;

            int count = skeleton.GetBoneCount();
            for (sbyte i = 0; i < count && i >= 0; i++)
            {
                if (skeleton.GetParentBoneIndex(i) < 0)
                {
                    _root = i;
                    break;
                }
            }

            if (_root < 0)
                VrLog.Warn("No parentless bone in the player skeleton, so the engine's root "
                           + "placement cannot be restored. A rider will sit on the horse's "
                           + "ground position while the camera stays correct.");

            return _root;
        }
        /// <summary>Said once per mission, so "the arms stopped following me on the
        /// horse" is a decision in the log rather than a mystery.</summary>
        private static void MountedOnce()
        {
            if (_loggedMounted)
                return;

            _loggedMounted = true;
            VrLog.Info("Mounted: hands and arms are left to the game's own animation, because "
                       + "motion_hands_mounted is off. It no longer needs to be - TEST 206 found "
                       + "the engine's root-bone placement was what the entitial rebuild was "
                       + "throwing away, and root_lift_restore puts it back.");
        }

        /// <summary>
        /// TEST 206: what is actually different about a rider.
        ///
        /// Three questions, one sample, and the answers want opposite fixes - which
        /// is the whole reason this is measured rather than reasoned about.
        ///
        /// 1. DO THE TWO ROUTES INTO WORLD SPACE AGREE? The hands convert their
        ///    target through entity.GetGlobalFrame(). The engine's own first-person
        ///    camera - the one that is already mount-correct - composes the head
        ///    bone through AgentVisuals.GetFrame() instead. If those two frames
        ///    differ for a rider, every mounted hand target is wrong by the gap and
        ///    the arm solve drags the shoulder after it. That is a one-line fix in
        ///    Drive and it has nothing to do with the rebuild.
        ///
        /// 2. DOES THE REBUILD ALONE DROP THE BODY? UpdateEntitialFramesFromLocalFrames
        ///    is called here with NOTHING written, which is the experiment the guard
        ///    above was built on but never ran. If the root's entitial frame comes
        ///    back lower than it went in, the saddle lift does not live in a local
        ///    frame and has to be put back by hand. If it comes back unchanged, that
        ///    entire theory is dead and the guard was costing us the mounted arms
        ///    for nothing.
        ///
        /// 3. IS THE HAND'S WORLD POSE ALREADY RIGHT? The anchor and a controller
        ///    are printed too. They come through VrCameraDriver, which is the same
        ///    path the eyes use, so they should already be in the saddle - and if
        ///    they are, the fault is entirely in the conversion below them.
        ///
        /// The rebuild is a real call and this really does touch the skeleton, so it
        /// is behind mounted_probe and off by default. It writes no bone of its own:
        /// rebuilding from the local frames the animation just set is what the
        /// engine is about to do anyway.
        /// </summary>
        private static void MountedProbe(Agent agent, MBAgentVisuals visuals, GameEntity entity,
                                         Skeleton skeleton, BvrInputState input)
        {
            if (!Probe)
                return;

            if (++_probeTick < 90)
                return;
            _probeTick = 0;

            try
            {
                Vec3 entityOrigin = entity.GetGlobalFrame().origin;
                Vec3 visualsOrigin = visuals.GetFrame().origin;
                Vec3 gap = visualsOrigin - entityOrigin;

                Agent mount = agent.MountAgent;
                float mountZ = mount != null ? mount.Position.z : float.NaN;

                // The head bone rather than a hand: it is the bone the engine's own
                // camera is built from, so a collapse shows up there at full size,
                // and it exists on every monster that can ride.
                sbyte head = agent.Monster != null ? agent.Monster.HeadLookDirectionBoneIndex : (sbyte)-1;

                sbyte root = RootBone(skeleton);
                MatrixFrame rootBefore = root >= 0
                    ? skeleton.GetBoneEntitialFrameWithIndex(root)
                    : MatrixFrame.Identity;
                MatrixFrame headBefore = head >= 0
                    ? skeleton.GetBoneEntitialFrameWithIndex(head)
                    : MatrixFrame.Identity;

                // ---- the experiment ------------------------------------------------
                skeleton.UpdateEntitialFramesFromLocalFrames();

                MatrixFrame rootAfter = root >= 0
                    ? skeleton.GetBoneEntitialFrameWithIndex(root)
                    : MatrixFrame.Identity;
                MatrixFrame headAfter = head >= 0
                    ? skeleton.GetBoneEntitialFrameWithIndex(head)
                    : MatrixFrame.Identity;

                // Put it back before anyone else reads the skeleton. The rebuild
                // above is a real one, and with the posing on, the arm solve reads
                // the shoulder a few lines later - against a body this probe had
                // just dropped, for one frame in ninety. Restoring here is what
                // lets the probe claim it leaves nothing behind.
                if (root >= 0)
                {
                    skeleton.SetBoneLocalFrame(root, rootBefore);
                    skeleton.UpdateEntitialFramesFromLocalFrames();
                }

                VrLog.Info(string.Format(
                    "TEST 206 mounted: agent z {0:F3}, mount z {1:F3}. "
                    + "entity.GetGlobalFrame ({2}), visuals.GetFrame ({3}), they differ by "
                    + "{4:F3} m ({5:F3} m of it height) - if that is the saddle height the "
                    + "hands are converting through the wrong frame. "
                    + "Rebuild with nothing written: root z {6:F3} -> {7:F3}, head bone z "
                    + "{8:F3} -> {9:F3} - if those move, the lift is not in the local frames. "
                    + "Anchor z {10:F3}, right controller z {11:F3} in world.",
                    agent.Position.z, mountZ,
                    F(entityOrigin), F(visualsOrigin),
                    gap.Length, gap.z,
                    rootBefore.origin.z, rootAfter.origin.z,
                    headBefore.origin.z, headAfter.origin.z,
                    VrCameraDriver.Anchor.origin.z,
                    input.Right.IsActive != 0
                        ? VrCameraDriver.StageToHand(input.Right.GripOrientation,
                                                     input.Right.GripPosition).origin.z
                        : float.NaN));
            }
            catch (Exception ex)
            {
                // A probe must never be the thing that takes a mission down.
                VrLog.Error("TEST 206: the mounted probe failed; nothing else is affected.", ex);
            }
        }

        private static string F(Vec3 v)
        {
            return v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00");
        }

        /// <summary>Whether every hand we intend to drive has a controller tracking
        /// it. The precondition for starting an automatic pass: one that begins
        /// with a controller asleep spends its window half blind.</summary>
        private static bool Tracking(BvrInputState input, VrHandSlot main, VrHandSlot off)
        {
            if (main.Valid && !TryController(input, main, out _))
                return false;

            if (off.Valid && !TryController(input, off, out _))
                return false;

            return main.Valid || off.Valid;
        }

        /// <summary>
        /// Watches or poses one hand, whichever it is owed. Returns whether it
        /// wrote a bone, so the caller knows whether the entitial frames need
        /// recomputing.
        /// </summary>
        private static bool Drive(Agent agent, MBAgentVisuals visuals, BvrInputState input,
                                  Skeleton skeleton, MatrixFrame agentGlobal, VrHandSlot slot)
        {
            if (!slot.Valid || !TryController(input, slot, out MatrixFrame world))
                return false;

            if (VrHandCalibration.SamplingFor(slot.Role))
            {
                // Left on its animation on purpose - that is the thing being
                // measured. One frame of the hand where the game put it is not
                // something anyone can see; two seconds of it is the price of a
                // calibration that does not disagree with itself.
                MatrixFrame animated = agentGlobal.TransformToParent(
                    skeleton.GetBoneEntitialFrameWithIndex(slot.Bone));

                VrHandCalibration.Observe(slot.Role, world, animated);

                // The same window is the only chance to measure where the weapon
                // sits relative to the bone: after this the weapon's frame is ours
                // and reading it back would return our own writing.
                ObserveBinding(agent, slot, animated);
                return false;
            }

            // Nothing captured and nothing saved: leave the arm on its animation.
            // Posing through a default grip would snap the hand to the controller
            // with an arbitrary rotation, which looks like a bug and is one - the
            // grip is not "no rotation", it is "not yet known".
            if (!VrHandCalibration.TryGrip(slot.Role, out MatrixFrame grip))
                return false;

            // THE MESH BINDING, MEASURED HERE RATHER THAN IN THE CALIBRATION WINDOW.
            //
            // It used to be taken only while a hand was being SAMPLED, which meant
            // it was never taken at all for anyone with a calibration already
            // saved - and the log showed exactly that shape of failure:
            //
            //   Grip calibration loaded for the Main hand
            //   ...no "Weapon entity write" line, ever
            //
            // A saved calibration means no sampling window, no binding, and
            // VrWeaponEntity.Drive returning early on every single frame. The
            // weapon was never written once.
            //
            // The window was never the point. What the binding needs is a frame
            // where the bone and the weapon are both still the ANIMATION's, and
            // every frame before our first write is such a frame - we do not write
            // the weapon until we have the binding, so this cannot read back its
            // own output.
            if (!VrWeaponEntity.HasBinding(slot.Role))
            {
                MatrixFrame animatedBone = agentGlobal.TransformToParent(
                    skeleton.GetBoneEntitialFrameWithIndex(slot.Bone));

                ObserveBinding(agent, slot, animatedBone);
            }

            world = world.TransformToParent(grip);

            // The slider offset, in the BODY's frame rather than the controller's.
            //
            // Applied through the anchor's rotation, which is the character's
            // heading, so "up" is up and "forward" is the way they are facing
            // however the player happens to be holding the controller. That is
            // what makes a slider labelled Z mean anything - see
            // VrHandCalibration.BodyOffset.
            Vec3 bodyOffset = VrHandCalibration.BodyOffset(slot.Role);
            if (bodyOffset.LengthSquared > 1e-8f)
                world.origin += VrCameraDriver.Anchor.rotation.TransformToParent(bodyOffset);

            // world -> agent entity. Entitial space is where the arm is solved, and
            // it is also the space SetBoneLocalFrame's parent walk starts from.
            //
            // NonOrthogonal, deliberately, and this one cost a measured 28 cm.
            //
            // MatrixFrame.TransformToLocal multiplies by the TRANSPOSE, which is the
            // inverse only when the rotation is orthonormal. An agent entity's frame
            // carries the agent's SCALE, so the plain call is wrong by a factor of
            // scale squared on the distance from the agent's origin - and since the
            // hand is over a metre above that origin, a scale of 1.1 put it 28 cm
            // too high. Measured in the log as a steady miss of (-0.13, 0.01, 0.28)
            // m, the same sign on BOTH hands, which is what said it was here and not
            // in the arm chain: a bone error would mirror between left and right.
            //
            // It hid for so long because the old one-bone path then applied the same
            // transpose a second time, on the way down to the parent bone, and the
            // two errors partly cancelled. Drawing the arm is what took the cancel
            // away.
            MatrixFrame inAgent = agentGlobal.TransformToLocalNonOrthogonal(world);

            // And the rotation comes back carrying 1/scale, which is an artifact
            // rather than anything real.
            //
            // A controller's orientation is a pure rotation, and in the entity's
            // frame it should still be one. But the inverse above has scale
            // 1/1.0615 for this agent, so the product with an orthonormal world
            // rotation lands at 0.942 - a 6 percent shrink written into the hand
            // bone every frame, which then propagates down the parent-local
            // conversions as a position error. This skeleton's entity-space frames
            // are unit scale (the shoulder rest sits at z = 1.414 and the entity
            // transform applies the 1.0615), so the scale does not belong here at
            // all and the honest thing is to drop it.
            inAgent.rotation.Orthonormalize();

            // The arm first, because it writes the hand itself - with the wrist
            // pinned to the target, which is the same thing the plain write below
            // does, reached through a solved shoulder and elbow instead of leaving
            // them wherever the animation had them. If it declines, nothing has
            // been written and the original one-bone path runs unchanged.
            VrArmChain chain;
            bool solved = VrArmBones.TryChainForHand(agent, visuals, skeleton, slot.Bone, out chain)
                          && VrArmIk.Solve(skeleton, chain, inAgent);

            if (!solved)
            {
                // Same correction as above: a bone frame carries scale too, so the
                // transpose is not its inverse.
                MatrixFrame parentFrame = skeleton.GetBoneEntitialFrameWithIndex(slot.Parent);
                MatrixFrame local = parentFrame.TransformToLocalNonOrthogonal(inAgent);

                skeleton.SetBoneLocalFrame(slot.Bone, local);
            }

            // Remembered, not checked here: the entitial frames are still last
            // frame's until UpdateEntitialFramesFromLocalFrames runs, so reading
            // the bone back at this point would measure nothing.
            if (_pendingCount < _pendingBone.Length)
            {
                _pendingBone[_pendingCount] = slot.Bone;
                _pendingWanted[_pendingCount] = inAgent;
                _pendingWorld[_pendingCount] = world;

                // The bone that is supposed to end up ON the controller. Once the
                // wrist is set back by the grip offset that is the ITEM bone, not
                // the hand - measuring the hand against the controller would now
                // report the offset itself as an error.
                _pendingGripBone[_pendingCount] =
                    (solved && chain.Item >= 0) ? chain.Item : slot.Bone;

                _pendingCount++;
            }

            // And the thing the player can actually see. The bone write above only
            // moves the skinned hand; the weapon is a separate entity that has to
            // be placed itself - see VrWeaponEntity for why the bone was never
            // going to carry it.
            VrWeaponEntity.Drive(agent, slot, world);
            return true;
        }

        /// <summary>
        /// Where the hand actually ended up, against where the controller is.
        ///
        /// WHY THIS IS TWO SEPARATE NUMBERS AND NOT ONE
        ///
        /// "The hand is in the wrong place" has two completely different causes and
        /// they need different fixes, so reporting a single error would just start
        /// an argument:
        ///
        ///   MISS is the bone against the target it was given. The arm solve pins
        ///   the wrist to the target, so this should be a fraction of a millimetre.
        ///   If it is not, the IK is at fault and no amount of calibration helps.
        ///
        ///   HEIGHT is the target against the player's own head, in the character's
        ///   frame. The hand is placed relative to the character's eye by however
        ///   far the controller is below the player's eye, so if the character's
        ///   eye anchor and the rendered view disagree vertically, the hands are
        ///   offset by exactly that much and the bones are innocent.
        ///
        /// The second one also distinguishes a CONSTANT offset from a SCALING
        /// error: reach out and back while watching it. Constant means the anchor
        /// is off and grip_offset_z corrects it exactly. Growing with reach means
        /// hand_scale or the eye height is wrong and an offset would only be right
        /// at one distance.
        /// </summary>
        private static void TraceAlignment(Agent agent, Skeleton skeleton, MatrixFrame agentGlobal)
        {
            if (!VrArmIk.Debug || _pendingCount == 0)
                return;

            if (++_alignTick < 90)
                return;
            _alignTick = 0;

            try
            {
                // Body axes, so "up" means up to the character rather than to the
                // world: the same frame the grip sliders are in.
                Mat3 body = VrCameraDriver.Anchor.rotation;

                for (int i = 0; i < _pendingCount; i++)
                {
                    MatrixFrame landed = agentGlobal.TransformToParent(
                        skeleton.GetBoneEntitialFrameWithIndex(_pendingGripBone[i]));

                    Vec3 miss = body.TransformToLocal(landed.origin - _pendingWorld[i].origin);

                    Vec3 fromHead = body.TransformToLocal(
                        _pendingWorld[i].origin - VrCameraDriver.HeadFrame.origin);

                    VrLog.Info(string.Format(
                        "Arm align [{0}]: bone misses its target by ({1:F3}, {2:F3}, {3:F3}) m in "
                        + "body axes (right, forward, up) - this should be ~0. Controller sits "
                        + "({4:F3}, {5:F3}, {6:F3}) m from the rendered head. Agent eye {7:F3}, "
                        + "anchor {8:F3}, head {9:F3} in world z.",
                        _pendingBone[i], miss.x, miss.y, miss.z,
                        fromHead.x, fromHead.y, fromHead.z,
                        agent.GetEyeGlobalPosition().z,
                        VrCameraDriver.Anchor.origin.z,
                        VrCameraDriver.HeadFrame.origin.z));
                }
            }
            catch (Exception)
            {
                // Diagnostics only; never take the hands down for a log line.
            }
        }

        /// <summary>Measures where this hand's weapon sits relative to its bone,
        /// during the one window the animation still owns both.</summary>
        private static void ObserveBinding(Agent agent, VrHandSlot slot, MatrixFrame boneWorld)
        {
            if (slot.Equipment == EquipmentIndex.None)
                return;

            WeakGameEntity weapon = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
            if (!weapon.IsValid)
                return;

            VrWeaponEntity.Observe(slot.Role, boneWorld, weapon.GetGlobalFrame());
        }

        /// <summary>
        /// The controller pose for a slot, in world space.
        ///
        /// Routed through the SAME anchor the eyes use. Any other route and the
        /// hands sit at an angle to the view that changes as you turn.
        /// </summary>
        private static bool TryController(BvrInputState input, VrHandSlot slot, out MatrixFrame world)
        {
            world = MatrixFrame.Identity;

            BvrHandState hand = slot.Controller == VrHand.Left ? input.Left : input.Right;
            if (hand.IsActive == 0)
                return false;

            world = VrCameraDriver.StageToHand(hand.GripOrientation, hand.GripPosition);
            return true;
        }

        /// <summary>
        /// Prints the whole skeleton once.
        ///
        /// Every step after this - which bone an item hangs from, which two bones
        /// the arm solve moves, which way their rest frames point - is a question
        /// about this table, and asking it from a log beats three rounds of
        /// inference. It is what turned the hand search from a guess about
        /// 'hand_R' into reading main_hand_bone out of the monster definition.
        /// </summary>
        private static void DescribeSkeletonOnce(Skeleton skeleton)
        {
            if (_loggedBones)
                return;

            _loggedBones = true;

            int count = skeleton.GetBoneCount();
            VrLog.Info("Player skeleton has " + count + " bone(s):");

            var line = new StringBuilder();

            for (sbyte i = 0; i < count && i >= 0; i++)
            {
                line.Length = 0;
                line.Append("  [").Append(i).Append("] ")
                    .Append(skeleton.GetBoneName(i) ?? "?")
                    .Append("  parent ").Append(skeleton.GetParentBoneIndex(i));

                VrLog.Info(line.ToString());
            }
        }

        public static void Reset()
        {
            _loggedDriving = false;
            _loggedMounted = false;
            _pendingCount = 0;
            _haveRoot = false;
            _root = -1;
            VrArmSeam.Reset();
            VrArmBones.Reset();
            VrArmIk.Reset();
            VrHandSlots.Reset();
        }
    }
}
