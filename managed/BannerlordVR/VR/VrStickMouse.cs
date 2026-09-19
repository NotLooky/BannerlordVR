using System;
using BannerlordVR.Interop;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// A mouse pointer, driven by a thumbstick, for menus.
    ///
    /// WHY A POINTER AND NOT MORE KEY BINDINGS
    ///
    /// Bannerlord's interface is only partly navigable by directional focus.
    /// Plenty of it - the map, the inventory grid, anything with a scrollbar or
    /// a drag - assumes a pointer, and no amount of up/down/left/right reaches
    /// those. Rather than pretend otherwise, this moves the actual cursor and
    /// lets every menu in the game work exactly the way it already does.
    ///
    /// It drives the ENGINE'S OWN cursor through Input.SetMousePosition rather
    /// than inventing a virtual one and overriding GetMousePositionX/Y. The
    /// difference matters: hit testing, hover states, drag handling and tooltips
    /// all read the engine's position through paths this mod never sees, and a
    /// private cursor would move a drawn arrow around while none of them agreed
    /// it had moved.
    ///
    /// WHY IT ONLY RUNS IN MENUS
    ///
    /// Because during play the right stick is the camera and the pointer is
    /// nothing - and moving the OS cursor under a game that has captured the
    /// mouse for look control is a good way to make the view drift. It follows
    /// the same UiWantsScreen answer everything else does, so there is one
    /// definition of "a menu is open" in the mod rather than three.
    /// </summary>
    public static class VrStickMouse
    {
        /// <summary>On, and LATCHED rather than momentary - see Active.</summary>
        public static bool Enabled { get; } = VrConfig.Bool("stick_mouse", true);

        /// <summary>Screen widths per second at full deflection. A pointer that
        /// crosses the screen in about a second is quick enough not to be a chore
        /// and slow enough to land on a button.</summary>
        private static readonly float Speed = VrConfig.Float("stick_mouse_speed", 1.1f);

        /// <summary>Below this the stick is at rest. Larger than the movement
        /// deadzone: a cursor that creeps while you read is worse than one that
        /// needs a definite push.</summary>
        private static readonly float Deadzone = VrConfig.Float("stick_mouse_deadzone", 0.20f);

        /// <summary>
        /// Which stick moves the pointer. LEFT by default.
        ///
        /// It was the right one first, on the reasoning that the left stick is
        /// still walking in any menu that leaves the character standing in the
        /// world. In practice the right stick is the CAMERA everywhere it is not
        /// a pointer - on the campaign map above all - and a stick that means
        /// "look" during play and "point" in a menu is one the hand has to think
        /// about. The left stick means "move a thing" in both, which is the
        /// nearer relative of moving a cursor.
        ///
        /// Set stick_mouse_stick = right to put it back.
        /// </summary>
        private static readonly bool UseRightStick =
            string.Equals(VrConfig.String("stick_mouse_stick", "left"), "right",
                          StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True while the pointer is genuinely under stick control.
        ///
        /// Read by VrInputManager, which has to report the mouse ACTIVE for the
        /// interface to be in pointer mode at all - Input.IsGamepadActive is
        /// IsControllerConnected AND NOT IsMouseActive, and the two modes are
        /// exclusive by construction. So this being true is what switches the
        /// game from focus navigation to a cursor, and it is deliberately only
        /// true while the stick is actually being pushed: let go and the pad
        /// takes back over, so both work without a setting.
        /// </summary>
        public static bool Active { get; private set; }

        /// <summary>
        /// Is the pointer holding this hand's stick this frame?
        ///
        /// WHY THE PAD HAS TO ASK
        ///
        /// Moving the cursor and reporting the stick are two different things,
        /// and for a while the mod did both at once. The stick drove the pointer
        /// AND was still published as ControllerRStick plus the four
        /// ControllerRStick* directions, so one push did three jobs: it moved the
        /// cursor, it swung the campaign map camera, and it scrolled whatever
        /// list was open. Aiming at a menu entry moved the entry.
        ///
        /// A stick can only mean one thing at a time. While the pointer has it,
        /// the pad reports it as centred and its directions as up - exactly the
        /// way the D-pad chord already borrows the same stick. The latch is what
        /// makes this safe to do: the pointer is switched on by this stick and
        /// off by the other one, so the camera is never taken away without a
        /// deliberate gesture, and the same gesture gives it back.
        /// </summary>
        public static bool OwnsStick(bool left)
        {
            // UseRightStick == true means the pointer is on the right hand, so
            // it owns this hand's stick exactly when the two disagree.
            return Active && UseRightStick != left;
        }

        private static float _x = -1f;
        private static float _y = -1f;
        private static bool _logged;
        private static bool _broken;

        public static void Tick(float dt)
        {
            if (!Enabled || _broken)
            {
                Active = false;
                return;
            }

            try
            {
                if (!InMenu() || !VrGamepad.Active)
                {
                    Active = false;
                    return;
                }

                // The D-pad chord borrows the RIGHT stick. Moving the pointer at
                // the same time would fight it - but only when the pointer is on
                // that stick too, which it no longer is by default.
                if (VrGamepad.DpadActive && UseRightStick)
                    return;

                if (!VrGamepad.TryGetStick(UseRightStick, out float sx, out float sy))
                {
                    Active = false;
                    return;
                }

                float mag = (float)Math.Sqrt(sx * sx + sy * sy);

                // THE MODE IS LATCHED, AND THAT IS THE WHOLE FIX.
                //
                // This used to be momentary: pointer while the stick was pushed,
                // pad the instant it was released. Since Input.IsGamepadActive is
                // IsControllerConnected AND NOT IsMouseActive, every one of those
                // transitions swapped the interface between pointer mode and
                // focus mode, and every swap drops the highlight. Pushing the
                // stick therefore produced a pointer that appeared, moved, and
                // vanished again - which reads as "not working" far more than it
                // reads as a mode.
                //
                // So the pointer is switched ON by the pointer stick and OFF by
                // the OTHER one. Nudging the movement stick, or any direction
                // that means "next item", hands the menu back to the pad. Two
                // deliberate gestures, no automatic flipping, and both ways of
                // working the same menu stay available.
                if (mag >= Deadzone)
                {
                    Active = true;
                }
                else if (Active && OtherStickPushed())
                {
                    // Back to directional navigation.
                    Active = false;
                    return;
                }

                if (!Active || mag < Deadzone)
                {
                    // Latched off, or latched on but resting: the pointer simply
                    // stays where it was left.
                    return;
                }

                float w = Screen.RealScreenResolutionWidth;
                float h = Screen.RealScreenResolutionHeight;
                if (!(w > 1f) || !(h > 1f))
                {
                    Active = false;
                    return;
                }

                // Seed from wherever the engine's cursor actually is, so taking
                // hold of it does not teleport the pointer across the screen.
                if (_x < 0f || _y < 0f)
                {
                    Vec2 at = Input.MousePositionPixel;
                    _x = at.x;
                    _y = at.y;
                }

                // Rescale the deflection so the deadzone is not a step: at the
                // threshold the pointer starts from zero rather than from 20% of
                // full speed, which is what makes fine positioning possible.
                float scaled = (mag - Deadzone) / Math.Max(1f - Deadzone, 1e-4f);
                float nx = sx / mag * scaled;
                float ny = sy / mag * scaled;

                float step = Speed * w * Math.Max(dt, 0f);

                _x += nx * step;

                // Stick up is +Y and screen down is +Y, so this is a subtraction.
                _y -= ny * step;

                _x = Clamp(_x, 0f, w - 1f);
                _y = Clamp(_y, 0f, h - 1f);

                Input.SetMousePosition((int)_x, (int)_y);
                Active = true;

                if (!_logged)
                {
                    _logged = true;
                    VrLog.Info("Stick mouse: the " + (UseRightStick ? "right" : "left")
                               + " stick moves the pointer in menus, and the triggers "
                               + "click. Let go and the pad takes back over, so menus can "
                               + "still be walked by direction. Set stick_mouse = 0 to "
                               + "turn it off.");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                Active = false;
                VrLog.Error("Stick mouse failed; menus keep the pad's directional "
                            + "navigation and the real mouse.", ex);
            }
        }

        /// <summary>
        /// Forgets where the pointer was, so the next menu re-seeds from the
        /// engine's own cursor rather than from a position that belonged to a
        /// screen that has since closed.
        /// </summary>
        public static void Reset()
        {
            _x = -1f;
            _y = -1f;
            Active = false;
        }

        /// <summary>
        /// Is the stick that is NOT driving the pointer being pushed? That is
        /// the gesture that hands the menu back to directional navigation.
        /// </summary>
        private static bool OtherStickPushed()
        {
            if (!VrGamepad.TryGetStick(!UseRightStick, out float x, out float y))
                return false;

            return (x * x + y * y) >= Deadzone * Deadzone;
        }

        /// <summary>Menus, by the same definition the rest of the mod uses.</summary>
        private static bool InMenu()
        {
            try
            {
                if (TaleWorlds.MountAndBlade.Mission.Current != null)
                    return VrMissionPhase.UiWantsScreen;

                // Outside a mission "no mission" used to be the whole test, which
                // made the campaign map a menu. It is not one: with nothing open
                // over it the right stick is the map camera, and taking that away
                // to move a pointer across empty terrain trades a control the
                // player wants for one they do not.
                return !MapCamera || PointerWanted();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Leave the bare campaign map's camera on the pointer stick, and take the
        /// stick only once something is actually open over it. Set
        /// stick_mouse_map_camera = 0 to go back to a pointer everywhere outside a
        /// mission.
        /// </summary>
        private static readonly bool MapCamera = VrConfig.Bool("stick_mouse_map_camera", true);

        /// <summary>
        /// Is a layer up that wants a cursor?
        ///
        /// The same question VrMissionPhase asks of a mission screen, asked of
        /// whatever is on top instead: a settlement menu, the inventory, the party
        /// screen and the encyclopedia all push a layer that takes focus or asks
        /// for the mouse, while the map's own furniture - nameplates, the top bar -
        /// does neither. So this comes out false on the bare map and true the
        /// moment there is something to point at, without naming a single screen.
        ///
        /// Unreadable means yes. A pointer that will not appear in the main menu is
        /// a mod that cannot be configured from inside the headset, which is far
        /// worse than a map camera that occasionally defers to the cursor.
        /// </summary>
        private static bool PointerWanted()
        {
            try
            {
                // ASK THE GAME STATE, NOT THE LAYERS.
                //
                // The first version of this walked the top screen's layers and
                // called it a menu if any of them took focus or asked for the
                // mouse. On the campaign map that is ALWAYS true - the map is
                // driven by clicking, so its own furniture asks for a cursor -
                // and the test came back "menu" on the bare map, which is the
                // one place it had to come back "no". The camera went dead.
                //
                // MapState answers the question directly and without inference:
                // it exists exactly while the campaign map is the active state,
                // and its menu context is non-null exactly while a settlement or
                // other game menu is open over it.
                GameStateManager states = GameStateManager.Current;
                GameState active = states == null ? null : states.ActiveState;

                if (active is MapState)
                {
                    Campaign campaign = Campaign.Current;
                    return campaign != null && campaign.CurrentMenuContext != null;
                }

                // Anything that is not the map and not a mission is a screen made
                // of interface: the main menu, the inventory, the party and clan
                // screens, the encyclopedia, options. All of them want a pointer,
                // and none of them has a camera to take away.
                return true;
            }
            catch
            {
                // Unreadable means yes. A pointer that will not appear in the main
                // menu is a mod that cannot be configured from inside the headset.
                return true;
            }
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
