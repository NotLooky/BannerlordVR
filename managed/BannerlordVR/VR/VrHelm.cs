using System;
using System.Diagnostics;
using System.Globalization;
using BannerlordVR.Interop;
using BannerlordVR.Patches;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 473: the War Sails tiller by hand. At the helm (vanilla Use), a grip
    /// within helm_reach_m of the tiller's hand point (the engine's own
    /// "hand_position", where the pilot's hand IK goes) takes it.
    ///
    /// TEST 473b, the user: "moving the hand forward steers right, pulling steers
    /// left", and "the hand gets stuck on the tiller when gripped". The hand's
    /// travel along the SHIP's forward axis from the grab sets the rudder over
    /// +/- helm_full_m, and while held the drawn hand is put on the tiller's
    /// handle (AdjustHand, from VrWeaponHands) - it rides the tiller as the rudder
    /// swings, wherever the controller goes. HelmPatch writes the rudder into the
    /// ship's input; the rowers and the sail stay the stick's. helm_sign flips it.
    ///
    /// Not inside VrGrab: that stands down while a machine is used (TEST 341),
    /// and the helm exists only then.
    /// </summary>
    internal static class VrHelm
    {
        private static readonly bool On = VrConfig.Bool("helm_hand", true);
        private static readonly bool Probe = VrConfig.Bool("helm_probe", true);
        private static readonly float Reach = VrConfig.Float("helm_reach_m", 0.25f);
        private static readonly float Full = VrConfig.Float("helm_full_m", 0.30f);
        private static readonly float Sign = VrConfig.Float("helm_sign", 1f) < 0f ? -1f : 1f;

        private const float Down = 0.7f;
        private const float Up = 0.4f;

        private static readonly bool[] _down = new bool[2];
        private static VrHand? _held;
        private static GameEntity _handle;
        private static Vec3 _grabLocal;
        private static float _f;
        private static long _logAt;
        private static bool _broken;

        internal static bool Held => _held != null;

        /// <summary>The hand's rudder, -1..1, linear in the hand.</summary>
        internal static float Fraction => _f;

        /// <summary>Whether this controller holds the tiller - its grip is then
        /// never the game's Attack (VrHolster.TriggerKeyAllowed).</summary>
        internal static bool Holds(bool left) => _held == (left ? VrHand.Left : VrHand.Right);

        /// <summary>The rudder input for a hand fraction: the engine squares it
        /// (Sign(r) * r^2), so the root keeps the hand linear. Pure.</summary>
        internal static float RudderFor(float f) => Math.Sign(f) * (float)Math.Sqrt(Math.Min(1f, Math.Abs(f)));

        internal static void Tick()
        {
            if (!On || _broken)
                return;

            try
            {
                if (NativeMethods.bvr_get_input_state(out BvrInputState input, battleGrip: true) != (int)BvrStatus.Ok)
                    return;

                Agent agent = Mission.Current?.MainAgent;
                object machine = HelmPatch.Machine;
                bool piloting = agent != null && machine is UsableMachine um && um.PilotAgent == agent;
                if (!piloting || VrWeaponHands.Paused())
                {
                    if (!piloting)
                        Let(null);
                    Follow(VrHand.Left, input.Left);
                    Follow(VrHand.Right, input.Right);
                    return;
                }

                object ship = HelmPatch.Ship(machine);
                if (!(ship is ScriptComponentBehavior shipScript))
                    return;

                MatrixFrame shipFrame = shipScript.GameEntity.GetGlobalFrame();
                shipFrame.rotation.Orthonormalize();
                GameEntity handle = HelmPatch.Handle(machine);

                Hand(VrHand.Left, input.Left, shipFrame, handle);
                Hand(VrHand.Right, input.Right, shipFrame, handle);

                if (_held == null)
                    return;

                VrHand hand = _held.Value;
                BvrHandState state = hand == VrHand.Left ? input.Left : input.Right;
                if (state.IsActive == 0)
                    return;

                Vec3 local = shipFrame.TransformToLocal(VrWeaponHands.HandPoint(hand, state));
                _f = Sign * Math.Max(-1f, Math.Min(1f, (local.y - _grabLocal.y) / Math.Max(0.05f, Full)));

                if (Probe && Stopwatch.GetTimestamp() >= _logAt)
                {
                    _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 473b helm: hand {0:F2} (forward +) -> rudder in {1:F2}, visual rudder {2:F3} (helm_sign "
                        + "{3:F0}; forward should turn the ship RIGHT).", _f, RudderFor(_f), HelmPatch.VisualRudder(ship), Sign));
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private static void Hand(VrHand hand, BvrHandState state, MatrixFrame shipFrame, GameEntity handle)
        {
            int i = (int)hand;
            if (state.IsActive == 0)
            {
                Follow(hand, state);
                return;
            }

            bool pressed = state.Trigger >= (_down[i] ? Up : Down);
            bool edge = pressed && !_down[i];
            bool released = !pressed && _down[i];
            _down[i] = pressed;

            if (released && _held == hand)
            {
                Let(hand);
                return;
            }

            if (!edge || _held != null || handle == null)
                return;

            Vec3 at = VrWeaponHands.HandPoint(hand, state);
            float d = at.Distance(handle.GetGlobalFrame().origin);
            if (d > Reach)
            {
                if (Probe)
                    VrLog.Info("TEST 473 helm: " + hand + " grip " + d.ToString("F2", CultureInfo.InvariantCulture)
                               + " m from the tiller (reach " + Reach.ToString("F2", CultureInfo.InvariantCulture) + ").");
                return;
            }

            _held = hand;
            _handle = handle;
            _grabLocal = shipFrame.TransformToLocal(at);
            _f = 0f;
            Pulse(hand, 0.5f, 0.05f);
            VrLog.Info("TEST 473 helm: " + hand + " took the tiller.");
        }

        private static void Follow(VrHand hand, BvrHandState state)
        {
            int i = (int)hand;
            _down[i] = state.Trigger >= (_down[i] ? Up : Down);
        }

        private static void Let(VrHand? hand)
        {
            if (_held == null)
                return;

            _held = null;
            _handle = null;
            _f = 0f;
            if (hand != null)
                Pulse(hand.Value, 0.25f, 0.03f);
            if (Probe)
                VrLog.Info("TEST 473 helm: let go" + (hand == null ? " (left the helm)" : "") + ".");
        }

        /// <summary>TEST 473b: from VrWeaponHands, on each hand's target before the
        /// arm solve - the hand on the tiller is drawn on its handle.</summary>
        internal static void AdjustHand(VrHand controller, ref MatrixFrame world)
        {
            if (_held != controller || _handle == null)
                return;

            try
            {
                world.origin = _handle.GetGlobalFrame().origin;
            }
            catch (Exception)
            {
                _handle = null;
            }
        }

        internal static void Fail(Exception ex)
        {
            _broken = true;
            _held = null;
            _handle = null;
            VrLog.Error("TEST 473: the hand on the helm failed; the helm is vanilla for this session.", ex);
        }

        private static void Pulse(VrHand hand, float amplitude, float seconds)
        {
            try
            {
                NativeMethods.bvr_trigger_haptic((int)hand, amplitude, seconds);
            }
            catch (Exception)
            {
                // A pulse is never worth a throw.
            }
        }
    }
}
