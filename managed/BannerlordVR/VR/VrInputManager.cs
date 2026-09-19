using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Puts the VR pad in front of the game's real input manager.
    ///
    /// WHY THIS SEAM AND NOT A HANDFUL OF PATCHES
    ///
    /// Everything the game asks about a key ends up at one object:
    ///
    ///     Input.IsKeyDown(k)        -> Input.InputManager.IsKeyDown(k)
    ///     Key.GetKeyState()         -> Input.GetKeyState(k) -> InputManager...
    ///     GameKey.GetKeyState(...)  -> ControllerKey.GetKeyState()
    ///     GameAxisKey               -> AxisKey.GetKeyState()
    ///     HotKeyManager, Gauntlet, the mission controller, the map screen
    ///
    /// Input.InputManager is a single static field holding an IInputManager. Six
    /// Harmony patches on Input's static methods would cover most of that and
    /// silently miss every caller that holds Input.InputManager and asks it
    /// directly - and there are such callers. Replacing the object covers both
    /// shapes at once, and it covers code written after this was, which patches
    /// on a fixed list of methods do not.
    ///
    /// It is also reversible in one assignment, which matters for something
    /// sitting in front of all keyboard and mouse input in a game people are
    /// playing. If anything here throws, Restore() puts the original back and the
    /// keyboard is exactly as it was.
    ///
    /// EVERY MEMBER FORWARDS
    ///
    /// This implements the whole interface and forwards all of it. Only the key
    /// queries do anything, and even those OR the pad in rather than replacing the
    /// answer - the keyboard has to keep working, both because the player may want
    /// it and because losing it inside a headset is not a recoverable situation.
    ///
    /// THE MOUSE HAND-OFF
    ///
    /// GameKey.GetKeyState only honours the controller binding while
    /// Input.IsGamepadActive, and Input computes that as
    ///
    ///     IsGamepadActive = IsControllerConnected AND NOT IsMouseActive
    ///
    /// so claiming a controller is connected is not enough on its own - an active
    /// mouse cancels it. That is the whole reason IsMouseActive is overridden
    /// here, and why it is done on a timer rather than outright: a player who puts
    /// the controllers down and picks up a mouse gets it back after a few seconds,
    /// and one who never does never notices.
    /// </summary>
    public sealed class VrInputManager : IInputManager
    {
        private readonly IInputManager _inner;

        private static VrInputManager _installed;
        private static IInputManager _original;
        private static FieldInfo _field;

        private static float _lastTicks;
        private static bool _brokenLogged;

        private VrInputManager(IInputManager inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// Wraps whatever is currently installed. Safe to call every frame - it
        /// returns immediately once it has taken, and it will not wrap itself.
        /// </summary>
        public static bool Install()
        {
            if (_installed != null)
                return true;

            if (!VrGamepad.Enabled)
                return false;

            try
            {
                // Plain reflection rather than one of Harmony's field-ref helpers.
                // Those hand back a `ref` and want a ref local to hold it, and a
                // one-shot assignment into a static field is not worth a language
                // version argument. This runs once.
                FieldInfo field = AccessTools.Field(typeof(Input), "_inputManager");
                if (field == null)
                {
                    if (!_brokenLogged)
                    {
                        _brokenLogged = true;
                        VrLog.Warn("Gamepad: Input._inputManager is not where it was, so the "
                                   + "controllers cannot drive the game. Keyboard and mouse "
                                   + "are untouched.");
                    }
                    return false;
                }

                var current = field.GetValue(null) as IInputManager;
                if (current == null)
                    return false;                 // Input.Initialize has not run yet.

                if (current is VrInputManager)
                {
                    _installed = (VrInputManager)current;
                    return true;
                }

                _original = current;
                _installed = new VrInputManager(current);
                field.SetValue(null, _installed);
                _field = field;

                VrLog.Info("Gamepad: installed in front of " + current.GetType().Name
                           + ". The Touch controllers now answer as an Xbox pad everywhere "
                           + "the game asks about one - movement, menus and dialogue alike.");
                return true;
            }
            catch (Exception ex)
            {
                if (!_brokenLogged)
                {
                    _brokenLogged = true;
                    VrLog.Error("Gamepad: could not take Input._inputManager, so the "
                                + "controllers will not drive the game. Keyboard and mouse "
                                + "are untouched.", ex);
                }
                return false;
            }
        }

        /// <summary>Puts the game's own input manager back. Shutdown, and the
        /// panic button if anything about this turns out to be a bad idea.</summary>
        public static void Restore()
        {
            if (_installed == null || _original == null || _field == null)
                return;

            try
            {
                _field.SetValue(null, _original);
                VrLog.Info("Gamepad: original input manager restored.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Gamepad: could not restore the original input manager.", ex);
            }
            finally
            {
                _installed = null;
                _original = null;
            }
        }

        // --- the four that do something --------------------------------------

        /// <summary>
        /// Called by Input.Update once a frame, at the top, before anything reads
        /// a key. That makes it the right and only place to sample the
        /// controllers: every query for the rest of the frame then sees one
        /// coherent snapshot, and the edges computed from it cannot land twice.
        /// </summary>
        public void UpdateKeyData(byte[] keyData)
        {
            try
            {
                VrGamepad.Sample(Elapsed());
            }
            catch (Exception ex)
            {
                if (!_brokenLogged)
                {
                    _brokenLogged = true;
                    VrLog.Error("Gamepad: sampling failed; the pad will be quiet.", ex);
                }
            }

            _inner.UpdateKeyData(keyData);

            // AFTER the inner call, which overwrites the array wholesale.
            VrGamepad.StampKeyData(keyData);
        }

        public Vec2 GetKeyState(InputKey key)
        {
            Vec2 real = _inner.GetKeyState(key);

            if (!VrGamepad.TryKeyState(key, out Vec2 vr))
                return real;

            // Whichever is pushed further. A physical pad and the VR one can both
            // be live, and picking the larger deflection means neither cancels the
            // other by reporting a resting zero.
            return (vr.x * vr.x + vr.y * vr.y) > (real.x * real.x + real.y * real.y) ? vr : real;
        }

        public bool IsKeyDown(InputKey key) => _inner.IsKeyDown(key) || VrGamepad.IsDown(key);

        public bool IsKeyDownImmediate(InputKey key) =>
            _inner.IsKeyDownImmediate(key) || VrGamepad.IsDown(key);

        public bool IsKeyPressed(InputKey key) =>
            _inner.IsKeyPressed(key) || VrGamepad.IsPressed(key);

        public bool IsKeyReleased(InputKey key) =>
            _inner.IsKeyReleased(key) || VrGamepad.IsReleased(key);

        public bool IsControllerConnected() =>
            _inner.IsControllerConnected() || VrGamepad.Active;

        /// <summary>
        /// Reports the mouse idle while the VR pad is in use, because
        /// Input.IsGamepadActive is IsControllerConnected AND NOT IsMouseActive
        /// and the controller bindings are dead without it. See the class note.
        /// </summary>
        /// <summary>
        /// Reports the mouse idle while the VR pad is in use, because
        /// Input.IsGamepadActive is IsControllerConnected AND NOT IsMouseActive
        /// and the controller bindings are dead without it.
        ///
        /// THE STICK MOUSE NO LONGER OVERRIDES THIS.
        ///
        /// It used to claim the mouse whenever the pointer was live, which did
        /// put the interface into pointer mode - and took it out of gamepad mode
        /// at the same time, because the two are exclusive by construction. The
        /// controller stopped being a controller: the face buttons went dead in
        /// menus and the on-screen prompts turned back into keyboard glyphs.
        /// A stick that moves a cursor is still a stick, and it should not cost
        /// the pad its identity.
        ///
        /// So the pointer now moves the engine's cursor and says nothing about
        /// the mouse. Whether Gauntlet hit-tests that cursor while in gamepad
        /// mode is the open question, and it is the one thing about this that
        /// cannot be answered from outside the engine.
        /// </summary>
        public bool IsMouseActive() =>
            _inner.IsMouseActive() && !VrGamepad.HasPriority;

        public Input.ControllerTypes GetControllerType() =>
            VrGamepad.HasPriority ? Input.ControllerTypes.Xbox : _inner.GetControllerType();

        /// <summary>
        /// Seconds since the previous sample, for the idle timer.
        ///
        /// Read off the wall clock rather than taken as a parameter, because the
        /// interface method this hangs off does not carry a delta and inventing a
        /// fixed one would drift with the frame rate.
        /// </summary>
        private static float Elapsed()
        {
            float now = Environment.TickCount * 0.001f;
            float dt = _lastTicks == 0f ? 0f : now - _lastTicks;
            _lastTicks = now;

            // TickCount wraps every 49.7 days and a huge or negative delta would
            // strand the pad on the wrong side of the priority timer.
            return dt < 0f || dt > 1f ? 0f : dt;
        }

        // --- pure forwarding --------------------------------------------------

        public float GetMousePositionX() => _inner.GetMousePositionX();
        public float GetMousePositionY() => _inner.GetMousePositionY();
        public float GetMouseScrollValue() => _inner.GetMouseScrollValue();
        public bool IsAnyTouchActive() => _inner.IsAnyTouchActive();
        public void PressKey(InputKey key) => _inner.PressKey(key);
        public void ClearKeys() => _inner.ClearKeys();
        public int GetVirtualKeyCode(InputKey key) => _inner.GetVirtualKeyCode(key);
        public void SetClipboardText(string text) => _inner.SetClipboardText(text);
        public string GetClipboardText() => _inner.GetClipboardText();
        public float GetMouseMoveX() => _inner.GetMouseMoveX();
        public float GetMouseMoveY() => _inner.GetMouseMoveY();
        public float GetNormalizedMouseMoveX() => _inner.GetNormalizedMouseMoveX();
        public float GetNormalizedMouseMoveY() => _inner.GetNormalizedMouseMoveY();
        public float GetGyroX() => _inner.GetGyroX();
        public float GetGyroY() => _inner.GetGyroY();
        public float GetGyroZ() => _inner.GetGyroZ();
        public float GetMouseSensitivity() => _inner.GetMouseSensitivity();
        public float GetMouseDeltaZ() => _inner.GetMouseDeltaZ();
        public Vec2 GetResolution() => _inner.GetResolution();
        public Vec2 GetDesktopResolution() => _inner.GetDesktopResolution();
        public void SetCursorPosition(int x, int y) => _inner.SetCursorPosition(x, y);
        public void SetCursorFriction(float frictionValue) => _inner.SetCursorFriction(frictionValue);
        public InputKey[] GetClickKeys() => _inner.GetClickKeys();

        public void SetRumbleEffect(float[] lowFrequencyLevels, float[] lowFrequencyDurations,
                                    int numLowFrequencyElements, float[] highFrequencyLevels,
                                    float[] highFrequencyDurations, int numHighFrequencyElements) =>
            _inner.SetRumbleEffect(lowFrequencyLevels, lowFrequencyDurations, numLowFrequencyElements,
                                   highFrequencyLevels, highFrequencyDurations, numHighFrequencyElements);

        public void SetTriggerFeedback(byte leftTriggerPosition, byte leftTriggerStrength,
                                       byte rightTriggerPosition, byte rightTriggerStrength) =>
            _inner.SetTriggerFeedback(leftTriggerPosition, leftTriggerStrength,
                                      rightTriggerPosition, rightTriggerStrength);

        public void SetTriggerWeaponEffect(byte leftStartPosition, byte leftEnd_position,
                                           byte leftStrength, byte rightStartPosition,
                                           byte rightEndPosition, byte rightStrength) =>
            _inner.SetTriggerWeaponEffect(leftStartPosition, leftEnd_position, leftStrength,
                                          rightStartPosition, rightEndPosition, rightStrength);

        public void SetTriggerVibration(float[] leftTriggerAmplitudes, float[] leftTriggerFrequencies,
                                        float[] leftTriggerDurations, int numLeftTriggerElements,
                                        float[] rightTriggerAmplitudes, float[] rightTriggerFrequencies,
                                        float[] rightTriggerDurations, int numRightTriggerElements) =>
            _inner.SetTriggerVibration(leftTriggerAmplitudes, leftTriggerFrequencies,
                                       leftTriggerDurations, numLeftTriggerElements,
                                       rightTriggerAmplitudes, rightTriggerFrequencies,
                                       rightTriggerDurations, numRightTriggerElements);

        public void SetLightbarColor(float red, float green, float blue) =>
            _inner.SetLightbarColor(red, green, blue);
    }
}
