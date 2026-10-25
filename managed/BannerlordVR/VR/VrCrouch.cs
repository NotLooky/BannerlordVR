using System;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 326. A real crouch is the game's crouch too.
    ///
    /// Past crouch_on_m of head drop below the standing neutral the agent is
    /// crouched the way the crouch key does it; above crouch_off_m it stands.
    /// Only crouches this class started are ended by it, so the key keeps
    /// working on its own.
    ///
    /// TEST 326b: the crouch is the SCRIPTED flag 0x200 (Agent.SetCrouchMode),
    /// not the Crouch event. MissionMainAgentController.ControlTick re-syncs
    /// every tick: CrouchMode differing from that flag sends Stand. 326 sent
    /// only the event, so the next tick stood the agent straight back up -
    /// CrouchMode never read true in the whole run.
    ///
    /// The view must not drop twice: the headset already carries the player
    /// down, and the engine's crouch lowers the eye the anchor is built from
    /// (the animated head, or CrouchEyeHeight in the formula). Lift() hands the
    /// anchor back the height the crouch took, measured against the eye height
    /// seen while standing, until the stand-up blend has finished too.
    /// </summary>
    internal static class VrCrouch
    {
        internal static readonly bool Enabled = VrConfig.Bool("crouch_physical", true);
        private static readonly float OnM = VrConfig.Float("crouch_on_m", 0.35f);
        private static readonly float OffM = VrConfig.Float("crouch_off_m", 0.25f);

        /// <summary>We sent the Crouch and have not yet seen the stand finish.</summary>
        private static bool _ours;
        private static bool _releasing;
        private static long _releaseUntil;

        private static float _standEye = -1f;

        /// <summary>From the ControlTick postfix, after the controller wrote its
        /// own flags for this tick.</summary>
        internal static void Tick(Agent agent)
        {
            if (!Enabled || agent == null || !agent.IsActive())
                return;

            try
            {
                // TEST 341: not at a helm, oar, rope or siege weapon - the crouch
                // key there is the machine's business.
                if (agent.HasMount || agent.IsUsingGameObject || !VrCameraDriver.RoomCalibrated
                    || !VrMissionPhase.VrDrivesLook)
                {
                    _ours = false;
                    return;
                }

                float drop = -VrCameraDriver.RoomOffset.z;

                if (!_ours && !agent.CrouchMode && drop > OnM && agent.IsCrouchingAllowed())
                {
                    _ours = true;
                    _releasing = false;
                    agent.SetCrouchMode(true);
                    agent.EventControlFlags |= Agent.EventControlFlag.Crouch;
                    VrLog.Info("TEST 326 crouch: head " + drop.ToString("F2") + " m down -> game crouch.");
                }
                else if (_ours && drop < OffM)
                {
                    _ours = false;
                    _releasing = true;
                    _releaseUntil = System.Diagnostics.Stopwatch.GetTimestamp()
                                    + System.Diagnostics.Stopwatch.Frequency;
                    agent.SetCrouchMode(false);
                    if (agent.CrouchMode)
                        agent.EventControlFlags |= Agent.EventControlFlag.Stand;
                    VrLog.Info("TEST 326 crouch: head " + drop.ToString("F2") + " m down -> stand.");
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 326: the physical crouch failed this tick.", ex);
            }
        }

        /// <summary>
        /// How far to raise the anchor this frame, given its height above the
        /// agent's feet. Zero unless our crouch is on or still blending back up.
        /// While standing, the height is remembered (eased) as the reference.
        /// </summary>
        internal static float Lift(Agent agent, float eyeAboveFeet)
        {
            if (!Enabled || agent == null || agent.HasMount)
                return 0f;

            if (!_ours && _releasing)
            {
                float left = _standEye - eyeAboveFeet;
                if (left < 0.01f || System.Diagnostics.Stopwatch.GetTimestamp() > _releaseUntil)
                    _releasing = false;
            }

            if (!_ours && !_releasing)
            {
                if (!agent.CrouchMode)
                    _standEye = _standEye < 0f ? eyeAboveFeet : _standEye + (eyeAboveFeet - _standEye) * 0.05f;
                return 0f;
            }

            // Capped: a death or a knock-down is not a crouch to undo.
            return _standEye > 0f ? Math.Min(0.9f, Math.Max(0f, _standEye - eyeAboveFeet)) : 0f;
        }

        internal static void Reset()
        {
            _ours = _releasing = false;
            _standEye = -1f;
        }
    }
}
