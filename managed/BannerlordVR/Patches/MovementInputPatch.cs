using System;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// The movement seam: head-relative rotation.
    ///
    /// TEST 291c. THE CALLER, NOT THE SETTER.
    ///
    /// This used to prefix Agent.MovementInputVector's setter. That prefix never
    /// ran: "Head-relative movement is on" is absent from every log, the setter is
    /// a one-line forward to native that the JIT inlines into its callers, and a
    /// Harmony patch on an inlined method is simply not reached. Movement only
    /// ever followed the head because the look did (VrHeadAim), and the engine
    /// walks relative to the look. TEST 291b holds the look on the body inside
    /// the deadzone, which is what exposed it.
    ///
    /// So the vector is rotated after MissionMainAgentController.ControlTick has
    /// written it - the one place the player's movement input is produced, and a
    /// method CombatControlPatch already patches successfully. Our own write goes
    /// through the setter from here, which is fine: nothing intercepts it now.
    /// </summary>
    [HarmonyPatch]
    public static class MovementInputPatch
    {
        private static bool Prepare()
        {
            if (!VrHeadMovement.Enabled && !VrRoomscale.Enabled && !VrCrouch.Enabled)
                return false;

            if (TargetMethod() != null)
                return true;

            VrLog.Warn("Head-relative movement: MissionMainAgentController.ControlTick is "
                       + "not where it was, so movement keeps following the game's own "
                       + "facing. Nothing else is affected.");
            return false;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(MissionMainAgentController), "ControlTick", new Type[0]);
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            // VrHeadMovement owns every decision and guard, including its own
            // exception handling.
            Agent agent = Mission.Current?.MainAgent;
            if (agent == null)
                return;

            Vec2 value = agent.MovementInputVector;

            // TEST 326: a real crouch sends the crouch key's own event, after the
            // controller has written this tick's flags.
            VrCrouch.Tick(agent);

            // TEST 297: a real step moves the body here, before the engine's own
            // agent tick, so it lands this frame - see VrRoomscale.Step. With it
            // on, VrRoomscale.Stick stays zero below.
            //
            // TEST 297c: not while the stick walks. A teleport restarts the
            // engine's locomotion, so stepping every frame held the stick walk at
            // ~2 mm a frame (log 01:46:47: stepped 0.14 m, walked 0.002) against
            // 35-40 mm with the stick alone. The real offset waits, and lands the
            // frame the stick is let go; the soft recentre covers a long wait.
            if (value.x == 0f && value.y == 0f)
                VrRoomscale.Step(agent);

            // TEST 296b: room scale joins here, after ControlTick's own per-axis
            // 0.2 deadzone, so a slow real step is a slow walk rather than stop-go.
            // In the head's axes already (VrRoomscale.Deliver), which is the frame
            // the correction below turns from.
            Vec2 room = VrMissionPhase.VrDrivesLook ? VrRoomscale.Stick : Vec2.Zero;
            Vec2 input = value;
            if (room.x != 0f || room.y != 0f)
            {
                input = new Vec2(value.x + room.x, value.y + room.y);
                float len = (float)Math.Sqrt(input.x * input.x + input.y * input.y);
                if (len > 1f)
                    input = new Vec2(input.x / len, input.y / len);
            }

            Vec2 corrected = VrHeadMovement.Correct(agent, input);

            // TEST 511: a hand on the ladder climbs it while the stick rests
            // (and TEST 510's probe, while climbing).
            corrected = VrLadder.Servo(agent, value, corrected);

            if (corrected.x != value.x || corrected.y != value.y)
                agent.MovementInputVector = corrected;
        }
    }
}
