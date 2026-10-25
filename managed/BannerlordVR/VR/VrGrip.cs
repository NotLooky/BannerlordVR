using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    internal static class VrGrip
    {
        internal static bool Toggle = VrConfig.Bool("grip_toggle", false);
        private static Agent _agent;
        private static Mission _mission;
        private static bool _observing;
        private static readonly Latch[] Hands = { new Latch(), new Latch() };

        // Shared by every battle grip owner; menus and the bowstring read physical input.
        internal static bool ConsumesPress(bool left) => Toggle && Hands[left ? 0 : 1].ReleasedByPress;

        internal static void Read(ref BvrInputState input, bool battle)
        {
            if (!Toggle && !_observing)
                return;
            _observing = true;
            Agent agent = Mission.Current?.MainAgent;
            if (!ReferenceEquals(agent, _agent) || !ReferenceEquals(Mission.Current, _mission))
            {
                _agent = agent;
                _mission = Mission.Current;
                Hands[0] = new Latch();
                Hands[1] = new Latch();
            }
            bool ready = agent != null && agent.IsActive() && agent.CombatActionsEnabled
                         && VrSystem.IsActive && VrMotionHands.Enabled && VrMissionPhase.VrDrivesLook
                         && Mission.Current.Mode != MissionMode.Conversation && Mission.Current.Mode != MissionMode.Barter
                         && !VrMissionPhase.UiMenuOpen && !VrMissionPhase.OrderMenuOpen && !VrMenu.Open && !VrWeaponHands.Paused();
            bool bow = agent?.WieldedWeapon.CurrentUsageItem?.WeaponClass == WeaponClass.Bow;
            Hand(ref input.Left, VrHand.Left, ready, bow, battle);
            Hand(ref input.Right, VrHand.Right, ready, bow, battle);
        }

        private static void Hand(ref BvrHandState state, VrHand hand, bool ready, bool bow, bool battle)
        {
            bool stringHand = bow && hand == (VrHandSlots.RidesOffWrist(_agent.WieldedWeapon.CurrentUsageItem)
                ? VrHandSlots.MainController : VrHandSlots.OffController);
            bool owned = _agent != null && _agent.IsActive()
                && (VrSiege.Holds(hand == VrHand.Left) || VrLadder.Holds(hand) || VrHelm.Holds(hand == VrHand.Left)
                    || VrRein.HeldBy == hand || VrTwoHand.Gripping == hand || VrWeight.SupportHand == hand
                    || (VrCrossbow.Active ? VrCrossbow.GripHeld(hand)
                        : VrGrab.Carried(_agent, hand) != EquipmentIndex.None));
            float value = Hands[(int)hand].Sample(state.Trigger, state.IsActive != 0,
                                                  Toggle && !stringHand, ready, owned);
            if (!battle || stringHand || value == state.Trigger)
                return;
            state.Trigger = value;
            state.Buttons = (state.Buttons & ~(int)BvrButton.Trigger)
                            | (value >= 0.7f ? (int)BvrButton.Trigger : 0);
        }

        internal sealed class Latch
        {
            private bool _down, _held, _toggle, _initialized, _suspended, _bridgeHold;
            internal bool ReleasedByPress { get; private set; }

            internal float Sample(float value, bool tracked, bool toggle, bool ready, bool owned)
            {
                bool down = value >= (_down ? 0.4f : 0.7f);
                if (!_initialized || toggle != _toggle)
                {
                    _bridgeHold = _initialized && !toggle && owned;
                    _initialized = true;
                    _toggle = toggle;
                    _held = owned;
                    _down = down;
                }
                if (ready && tracked && down)
                    _bridgeHold = false;
                if (toggle && ready && tracked && !_suspended)
                {
                    if (down && !_down)
                    {
                        ReleasedByPress = _held;
                        _held = !_held;
                    }
                    else if (!down && !owned)
                        _held = false; // A missed grab must not consume the next press.
                }
                if (!down)
                    ReleasedByPress = false;
                _down = down;
                _suspended = !ready || !tracked;
                return toggle ? (_held ? 1f : 0f) : _bridgeHold && owned ? 1f : value;
            }
        }
    }
}
