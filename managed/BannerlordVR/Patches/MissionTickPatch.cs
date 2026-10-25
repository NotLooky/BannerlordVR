using System;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using BannerlordVR.VR;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// Poses the hands and the weapons, at the latest per-frame seam there is.
    ///
    /// WHY NOT OnFrameTick, WHERE IT WAS
    ///
    /// The write worked and the weapon still did not move, and the log finally
    /// separated those two facts:
    ///
    ///     Weapon entity write CONFIRMED: read straight back 0.000 m from where
    ///     it was put, so the frame takes.
    ///
    /// SetGlobalFrame is not being refused. The frame is set, reads back exactly,
    /// and is then thrown away before anything is drawn - because OnFrameTick runs
    /// BEFORE the mission tick, the mission tick animates the skeleton, and the
    /// engine resolves every attached entity's transform from the bone afterwards.
    /// Our write is simply the earliest of three, and the last one wins.
    ///
    /// The same is true of the bone write, for the same reason, which is why
    /// neither approach ever moved anything: both were landing upstream of the
    /// animation that overwrote them.
    ///
    /// WHY THIS ONE
    ///
    /// MissionScreen's handler interface exposes the frame in order:
    ///
    ///     BeforeMissionTick  ->  [mission tick: agents animate]  ->  AfterMissionTick
    ///
    /// AfterMissionTick is by construction after the animation and after the
    /// attachment resolve, and unlike OnSceneRenderingStarted - which was the
    /// previous attempt and turned out to fire once per SCENE - it is per frame.
    /// That is the whole of the requirement: after the animation, every frame.
    ///
    /// It is patched rather than the interface method because the explicit
    /// implementation forwards to this protected virtual one, and a name is a
    /// steadier patch target than an explicitly implemented interface slot.
    /// </summary>
    [HarmonyPatch(typeof(MissionScreen), "AfterMissionTick",
                  new Type[] { typeof(Mission), typeof(float) })]
    public static class MissionTickPatch
    {
        private static bool _brokenLogged;
        private static bool _loggedFirst;
        private static int _censusAt;

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                if (!VrSystem.IsActive)
                    return;

                // TEST 342: A MISSION BEING TORN DOWN IS NOT ONE TO ASK QUESTIONS OF.
                // Quit to main menu crashed in VrMelee.Tick: the screen ticked once
                // more after the mission was finalized, and VrEnemyGuard's
                // FindAgentWithIndex handed native a null mission pointer
                // (MBMission.find_agent_with_index, callback 474, read 0x62DA44).
                // An access violation is not a managed exception - the catch
                // below could not save it. VrMissionPhase already holds to the
                // same rule.
                Mission mission = Mission.Current;
                if (mission == null || mission.IsFinalized || mission.CurrentState != Mission.State.Continuing)
                    return;

                VrBreadcrumb.Set(VrBreadcrumb.MissionTick);

                // TEST 452: the controllers located on THIS thread for this frame,
                // before anything poses a hand - see VrWeaponHands.LatchInput.
                VrWeaponHands.LatchInput();

                // TEST 420: names the scene beside the native 5 s perf lines, so a
                // tavern, a street and a battle can be told apart by timestamp.
                int now = Environment.TickCount;
                if (now - _censusAt >= 5000)
                {
                    _censusAt = now;
                    VrLog.Info($"Scene census (TEST 420): {mission.SceneName}, {mission.Agents.Count} agent(s).");
                }

                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    VrLog.Info("Hands and weapons are now posed from AfterMissionTick - after "
                               + "the animation, every frame. The two earlier seams were both "
                               + "upstream of the animation that overwrote them: OnFrameTick "
                               + "runs before the mission tick, and OnSceneRenderingStarted "
                               + "fires once per scene rather than per frame.");
                }

                // BEFORE the hands, and after the mission tick, which is the
                // point of moving it here.
                //
                // The aim used to be written at the tail of UpdateCamera, which
                // runs before the mission tick - so the controller recomputed
                // LookDirection immediately afterwards and the write was thrown
                // away. The probe measured 13 to 56 degrees of drift per frame
                // against an expected one or two, and the symptom was the body
                // and weapon refusing to follow the head however far it turned.
                //
                // Exactly the fault this patch already exists to fix for the
                // weapon: a correct write landing upstream of the thing that
                // overwrites it.

                // VrDrivesLook: this steers the AGENT, and during deployment
                // there is no agent to steer and no look of ours to impose.
                if (VrMissionPhase.VrDrivesLook)
                {
                    VrBreadcrumb.Set(VrBreadcrumb.HeadAim);
                    VrHeadAim.Apply();
                }

                // TEST 384: a held (paused) skeleton whose hands pass stopped is let go.
                VrBodyFreeze.IdleGuard();

                // The hands and the weapon are the experimental half. Off by
                // default; the gamepad emulation is a separate path and is not
                // affected by this. See VrMotionHands.
                if (VrMotionHands.ShouldPose)
                {
                    // TEST 332: before the hands, so a weapon picked up this frame
                    // is copied and glided by this frame's weapon drive.
                    VrBreadcrumb.Set(VrBreadcrumb.Holster);
                    VrGrab.Tick();

                    VrBreadcrumb.Set(VrBreadcrumb.WeaponHands);
                    VrWeaponHands.Tick();

                    // TEST 328b: the holstered weapons hang from the bones just drawn.
                    VrBreadcrumb.Set(VrBreadcrumb.Holster);
                    VrHolsterView.Tick();

                    // TEST 470/471: the rope hand's gestures, then the rope it holds.
                    VrRein.Tick();
                    VrRope.Tick();

                    // TEST 377d: the wrist HUDs hang from the same drawn arms.
                    VrWristHud.AfterArms();

                    // TEST 372: let-go weapons fly home to the holsters just hung.
                    VrThrow.Tick();
                }

                // AFTER the weapon drives, which is the whole point of the
                // seam: a later phase reads the blade from the entity frame
                // this tick just wrote. Phase 0 only listens for its test key -
                // and it is deliberately outside the ShouldPose gate, because
                // proving the damage path must not depend on the hand posing
                // working. See VrPhysicalCombat.
                //
                // TEST 245's gesture recogniser sits beside it, for the same
                // reason: the controllers are the input whatever the posing is
                // doing. It writes nothing here - the bits go in from the game's
                // own control tick, through CombatControlPatch.
                VrBreadcrumb.Set(VrBreadcrumb.Melee);
                VrMelee.Tick();

                VrBreadcrumb.Set(VrBreadcrumb.PhysCombat);
                VrPhysicalCombat.Tick();

                // TEST 269: manual archery. VrMelee stands down for bows, this
                // picks them up; its attack mask goes in through CombatControlPatch.
                VrBreadcrumb.Set(VrBreadcrumb.Bow);
                VrBow.Tick();

                // TEST 373: the manual crossbow, after VrBow (which leaves the
                // arrow model and the drawn bow to it while a crossbow is out).
                VrCrossbow.Tick();

                // TEST 473: the tiller by hand - outside VrGrab, which stands
                // down while a machine is used.
                VrHelm.Tick();

                // TEST 512: siege machines by hand, after VrWeaponHands learnt the handles.
                VrSiege.Tick();

                // TEST 271: body holsters, after VrBow so the quiver hand is known.
                VrBreadcrumb.Set(VrBreadcrumb.Holster);
                VrHolster.Tick();
                DropPatch.Track();

                // TEST 254: room scale, AFTER the mission tick, which is where the
                // avatar's own travel for this frame already exists - the offset is
                // consumed by what the agent actually moved, not by what it was
                // asked to. It publishes the input the movement seam adds next
                // frame; see VrRoomscale.
                VrBreadcrumb.Set(VrBreadcrumb.Roomscale);
                VrRoomscale.Tick(Mission.Current?.MainAgent);

                // Back to the coarse crumb: anything faulting after this point
                // is no longer inside one of ours.
                VrBreadcrumb.Set(VrBreadcrumb.MissionTick);
            }
            catch (Exception ex)
            {
                // Never take the mission down. Report once, then stay quiet.
                if (!_brokenLogged)
                {
                    _brokenLogged = true;
                    VrLog.Error("AfterMissionTick postfix failed; hands and weapons go back to "
                                + "the game's own animation.", ex);
                }
            }
        }
    }
}
