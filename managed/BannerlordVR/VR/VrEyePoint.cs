using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Where the player's eyes actually are.
    ///
    /// WHAT WAS WRONG
    ///
    /// The anchor used Agent.GetEyeGlobalPosition(). That reads plausible and is
    /// the wrong function: the game itself never uses it as a camera position.
    /// MissionScreen.UpdateCamera calls it in exactly one place - to place the
    /// AUDIO LISTENER:
    ///
    ///     Vec3 val28 = agentToFollow.GetEyeGlobalPosition();
    ///     ...
    ///     Mission.SetCameraFrame(ref val, ..., ref val28);
    ///
    /// Its own first-person CAMERA is built somewhere else entirely, out of the
    /// head bone:
    ///
    ///     skeleton.ForceUpdateBoneFrames();
    ///     head = agent.GetBoneEntitialFrame(Monster.HeadLookDirectionBoneIndex, true);
    ///     head.origin = head.TransformToParent(Monster.FirstPersonCameraOffsetWrtHead);
    ///     origin = agent.AgentVisuals.GetFrame().TransformToParentDouble(head.origin);
    ///
    /// The difference does not matter much on foot, which is why this survived
    /// this long. It matters enormously on a horse. The eye position is derived
    /// from the agent's own ground position plus an eye height, and a mounted
    /// rider's ground position is the HORSE's - so the eye lands roughly a
    /// standing man's height above the ground the horse is standing on, which is
    /// somewhere around its shoulder. That is the "stuck inside the horse"
    /// report, exactly: not a stuck camera, a camera placed as though the rider
    /// were standing where the horse is.
    ///
    /// The head bone has no such problem, because it is not computed from a
    /// position and a height at all. The rider's skeleton is placed in the saddle
    /// by the engine, so its head bone is in the saddle too, at whatever height
    /// the animal and the animation put it. Mounted and on foot are not two cases
    /// here - they are the same case, and that is the point of using it.
    ///
    /// WHY THE GAME'S OWN ROUTE RATHER THAN A CORRECTION
    ///
    /// A mount offset could have been added to the old value instead. That would
    /// have been a second guess stacked on the first: a saddle's height is not a
    /// constant, it depends on the animal, on the animation and on the terrain
    /// under each hoof. Reading the bone asks the engine where the head is rather
    /// than predicting it, and the engine is never wrong about that.
    /// </summary>
    public static class VrEyePoint
    {
        /// <summary>
        /// Which of the three eye points to anchor to.
        ///
        ///   head    the game's own first-person camera point. Mount-correct
        ///           because it comes from the skeleton rather than from a ground
        ///           position, and it moves with the animation exactly as the
        ///           game's own first-person view does.
        ///   stable  MBAgentVisuals.GetGlobalStableEyePoint. Also taken from the
        ///           visuals, so also mount-correct, and named for being steadier
        ///           under animation. Worth trying if head bob is uncomfortable.
        ///   eye     Agent.GetEyeGlobalPosition - the old behaviour, kept so the
        ///           horse fault can be reproduced rather than taken on trust.
        /// </summary>
        private static readonly string Source =
            VrConfig.String("anchor_eye", "head").Trim().ToLowerInvariant();

        private static bool _loggedSource;
        private static bool _loggedCompare;
        private static bool _warnedNoBone;
        private static bool _broken;

        /// <summary>
        /// The eye position to anchor to, or the agent's own eye position if
        /// anything at all is unavailable this frame.
        ///
        /// Never throws. An anchor is needed every frame and a missing bone is no
        /// reason to stop rendering, so every failure here degrades to the old
        /// value rather than propagating.
        /// </summary>
        public static Vec3 Resolve(Agent agent)
        {
            Vec3 legacy = agent.GetEyeGlobalPosition();

            if (_broken || Source == "eye")
            {
                LogSourceOnce("eye",
                    "Agent.GetEyeGlobalPosition - the old behaviour, which sits inside the horse when mounted");
                return legacy;
            }

            try
            {
                MBAgentVisuals visuals = agent.AgentVisuals;
                if (visuals == null || !visuals.IsValid())
                    return legacy;

                if (Source == "stable")
                {
                    Vec3 stable = visuals.GetGlobalStableEyePoint(agent.IsHuman);
                    if (!IsUsable(stable))
                        return legacy;

                    LogSourceOnce("stable", "MBAgentVisuals.GetGlobalStableEyePoint");
                    Compare(agent, legacy, stable);
                    return stable;
                }

                if (Source == "bone")
                {
                    Vec3 bone;
                    if (!HeadPoint(agent, visuals, out bone))
                        return legacy;

                    LogSourceOnce("bone", "the head bone's own entitial frame");
                    Compare(agent, legacy, bone);
                    return bone;
                }

                Vec3 head;
                if (!CameraHeight(agent, out head))
                    return legacy;

                LogSourceOnce("head",
                    "the game's own first-person camera height above the agent");
                Compare(agent, legacy, head);
                return head;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Eye point: reading the head bone failed, so the anchor falls back to "
                            + "Agent.GetEyeGlobalPosition for the rest of the session. Mounted "
                            + "views will sit low again.", ex);
                return legacy;
            }
        }

        /// <summary>
        /// The game's own first-person eye height, above the agent's position.
        ///
        /// WHY THIS AND NOT THE HEAD BONE
        ///
        /// The head bone was the first attempt and it was wrong in a way the log
        /// caught, once it was asked to say so:
        ///
        ///   the old Agent.GetEyeGlobalPosition said (423.81,765.31,16.44),
        ///   the one now in use says (434.23,759.62,17.67). Difference 11.93 m
        ///
        /// Nearly twelve metres, of which only 1.23 m was height. An eye point
        /// cannot be twelve metres from the agent it belongs to, so the bone was
        /// being read in one space and composed in another - and the result was
        /// an anchor beside the horse rather than on it, which is why the hands
        /// were nowhere near the player.
        ///
        /// Rather than work out which of GetBoneEntitialFrame's spaces is which,
        /// this takes the formula MissionScreen itself uses, which needs no
        /// skeleton at all. From TaleWorlds.MountAndBlade.View.Screens.
        /// MissionScreen.UpdateCamera, where it appears twice - once for the
        /// third-person follow target and once for vertical aim correction - and
        /// both copies agree:
        ///
        ///   mounted:
        ///     (mount.RiderCameraHeightAdder + mount.BodyCapsulePoint1.z
        ///      + mount.BodyCapsuleRadius) * mountScale
        ///     + rider.CrouchEyeHeight * riderScale
        ///
        ///   on foot:
        ///     (crouching ? CrouchEyeHeight : StandingEyeHeight) * riderScale
        ///
        /// added to Agent.Position. On a horse Agent.Position IS the horse's
        /// ground position - that is the whole reason the old anchor sat inside
        /// the animal - and the mounted formula is written to be added to exactly
        /// that. So the thing that made GetEyeGlobalPosition wrong is what makes
        /// this right.
        ///
        /// It is also STEADIER than a bone. A head bone bobs with the walk cycle
        /// and swings with every animation; this is a function of the mount and
        /// the rider and nothing else, so the horizon does not lurch each time the
        /// horse takes a stride.
        /// </summary>
        private static bool CameraHeight(Agent agent, out Vec3 point)
        {
            point = default(Vec3);

            Monster monster = agent.Monster;
            if (monster == null)
                return false;

            float scale = agent.AgentScale;
            float height;

            Agent mount = agent.MountAgent;
            if (agent.HasMount && mount != null && mount.Monster != null)
            {
                Monster m = mount.Monster;

                // The rider sits at the top of the mount's body capsule plus
                // whatever the mount declares as its rider-camera adder, and then
                // the rider's own CROUCH eye height on top - crouched because a
                // rider is seated, which is the same posture as far as the eye
                // height is concerned. All of that is the game's choice, not ours.
                height = (m.RiderCameraHeightAdder + m.BodyCapsulePoint1.z + m.BodyCapsuleRadius)
                         * mount.AgentScale
                         + monster.CrouchEyeHeight * scale;
            }
            else
            {
                height = (agent.CrouchMode ? monster.CrouchEyeHeight : monster.StandingEyeHeight)
                         * scale;
            }

            Vec3 world = agent.Position;
            world.z += height;

            if (!IsUsable(world))
                return false;

            point = world;
            return true;
        }

        /// <summary>
        /// The head bone's own entitial frame. Kept behind anchor_eye = bone
        /// because it is what the previous attempt did and the comparison is
        /// worth being able to make from the config rather than from a rebuild -
        /// but see CameraHeight for why it is not the default.
        /// </summary>
        private static bool HeadPoint(Agent agent, MBAgentVisuals visuals, out Vec3 point)
        {
            point = default(Vec3);

            Monster monster = agent.Monster;
            if (monster == null)
                return false;

            sbyte bone = monster.HeadLookDirectionBoneIndex;
            if (bone < 0)
            {
                if (!_warnedNoBone)
                {
                    _warnedNoBone = true;
                    VrLog.Warn("Eye point: this monster declares no head_look_direction_bone, so "
                               + "the first-person point cannot be built from the skeleton. "
                               + "Falling back to Agent.GetEyeGlobalPosition, which sits low "
                               + "when mounted.");
                }
                return false;
            }

            Skeleton skeleton = visuals.GetSkeleton();
            if (skeleton == null)
                return false;

            skeleton.ForceUpdateBoneFrames();

            MatrixFrame head = agent.GetBoneEntitialFrame(bone, true);

            // The offset is expressed in the HEAD's own frame - it is where the
            // eyes sit relative to the skull - so it has to be rotated by the head
            // before it means anything in the body's frame.
            head.origin = head.TransformToParent(monster.FirstPersonCameraOffsetWrtHead);

            Vec3 world = visuals.GetFrame().TransformToParentDouble(head.origin);
            if (!IsUsable(world))
                return false;

            point = world;
            return true;
        }

        /// <summary>
        /// Guards against the engine handing back an invalid frame mid-spawn.
        ///
        /// A NaN here would not fail loudly. It would propagate into the anchor,
        /// then into both eye cameras, and the headset would simply go black with
        /// nothing in any log to say why.
        /// </summary>
        private static bool IsUsable(Vec3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
                && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }

        private static void LogSourceOnce(string name, string what)
        {
            if (_loggedSource)
                return;

            _loggedSource = true;
            VrLog.Info("Eye point: anchor_eye = " + name + ", so the view sits at " + what + ".");
        }

        /// <summary>
        /// Prints both answers once, the first time the player is mounted.
        ///
        /// Mounted is the only state in which they disagree by enough to see, and
        /// this is what turns "the horse fix works" into a number. A gap of about
        /// a metre of height is the bug, measured.
        /// </summary>
        private static void Compare(Agent agent, Vec3 legacy, Vec3 chosen)
        {
            if (_loggedCompare || !agent.HasMount)
                return;

            _loggedCompare = true;

            Vec3 d = chosen - legacy;
            VrLog.Info("Eye point, mounted: the old Agent.GetEyeGlobalPosition said ("
                       + F(legacy) + "), the one now in use says (" + F(chosen)
                       + "). Difference " + d.Length.ToString("0.00") + " m, of which "
                       + d.z.ToString("0.00") + " m is height - that height is how far "
                       + "inside the horse the old anchor was sitting.");
        }

        private static string F(Vec3 v)
        {
            return v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00");
        }

        /// <summary>Per-mission state, so the mounted comparison prints again in
        /// the next battle rather than once per process.</summary>
        public static void Reset()
        {
            _loggedCompare = false;
        }
    }
}
