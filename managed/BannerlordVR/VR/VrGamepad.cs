using System;
using BannerlordVR.Interop;
using TaleWorlds.InputSystem;
using TaleWorlds.MountAndBlade;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The Touch controllers, as an Xbox pad.
    ///
    /// WHY A PAD AND NOT A SET OF BESPOKE VR CONTROLS
    ///
    /// Because the game already has a complete, tested, remappable control scheme
    /// for a pad, and building a second one would mean re-answering every question
    /// it has already answered - what closes a menu, what accepts a dialogue line,
    /// what scrolls an inventory. Bannerlord registers its movement as
    ///
    ///     new GameAxisKey("MovementAxisX", InputKey.ControllerLStick, ...)
    ///     new GameKey(0, "Up", "Generic", InputKey.W, InputKey.ControllerLStickUp, ...)
    ///
    /// so a left stick that reports itself as ControllerLStick moves the player,
    /// and one that also reports ControllerLStickUp works the menus, with no
    /// knowledge of either on our side. Pretending to be the pad the game already
    /// supports is a much smaller claim than inventing a control scheme.
    ///
    /// WHERE THIS GETS PLUGGED IN
    ///
    /// Not here. This class only decides what the pad WOULD say; VrInputManager
    /// is what makes the game ask. See that file for why the seam is where it is.
    ///
    /// EDGES ARE OURS TO KEEP
    ///
    /// IsKeyPressed and IsKeyReleased are edge queries, and the real input manager
    /// cannot answer them for buttons it has never seen. So this holds two frames
    /// of state and computes its own edges, sampled exactly once per frame from
    /// UpdateKeyData - which the engine calls once per frame, at the top, before
    /// anything reads a key. Sampling anywhere else risks a button that is pressed
    /// twice or not at all depending on which system asked first.
    /// </summary>
    public static class VrGamepad
    {
        public static bool Enabled { get; } = VrConfig.Bool("gamepad", true);

        /// <summary>
        /// Below this a stick is at rest. Touch sticks do not return exactly zero
        /// and a character that drifts north while you are reading a menu is worse
        /// than one that needs a firm push.
        /// </summary>
        private static readonly float Deadzone = VrConfig.Float("gamepad_deadzone", 0.18f);

        /// <summary>How far a stick must go before it also counts as a DIGITAL
        /// direction. Menu navigation reads the digital keys, so this is "how far
        /// is a flick", and it wants to be well clear of the deadzone.</summary>
        private static readonly float DigitalThreshold =
            VrConfig.Float("gamepad_digital", 0.6f);

        /// <summary>Trigger travel that counts as a press for the digital
        /// ControllerLTrigger / ControllerRTrigger keys.</summary>
        private static readonly float TriggerThreshold =
            VrConfig.Float("gamepad_trigger", 0.5f);

        /// <summary>Grip travel that counts as a bumper press.</summary>
        private static readonly float GripThreshold = VrConfig.Float("gamepad_grip", 0.5f);

        /// <summary>
        /// How long after the last touch of a controller the VR pad still claims
        /// priority over the mouse. See VrInputManager.IsMouseActive - the game
        /// only honours controller keys while Input.IsGamepadActive, and that is
        /// IsControllerConnected AND NOT IsMouseActive.
        ///
        /// 0 means "always", which is right for a player who never takes the
        /// headset off and wrong for one who is debugging on the monitor with a
        /// mouse in hand. A few seconds lets both work without a setting.
        /// </summary>
        private static readonly float PrioritySeconds =
            VrConfig.Float("gamepad_priority_seconds", 3f);

        // 256 because InputKey is a byte-ranged enum and the controller keys live
        // at 222..255. A flat array indexed by the key is the whole lookup.
        private const int KeyCount = 256;

        private static readonly bool[] Down = new bool[KeyCount];
        private static readonly bool[] Was = new bool[KeyCount];
        private static readonly Vec2[] Analog = new Vec2[KeyCount];

        private static bool _tracked;
        private static float _idleSeconds = float.MaxValue;
        private static bool _loggedFirst;
        private static bool _broken;

        /// <summary>True when the controllers are tracking and the pad is on, so
        /// the game should be told a controller is connected.</summary>
        public static bool Active => Enabled && !_broken && _tracked;

        /// <summary>
        /// True while the player is actually using the controllers, as opposed to
        /// merely wearing them. Drives the mouse hand-off.
        /// </summary>
        public static bool HasPriority =>
            Active && (PrioritySeconds <= 0f || _idleSeconds < PrioritySeconds || InMenu());

        /// <summary>
        /// A menu holds the pad's priority open regardless of the idle timer.
        ///
        /// The timer exists so someone debugging on the monitor with a mouse in
        /// hand can take control back, and that is worth keeping during play.
        /// In a menu it is actively harmful: stop to read for three seconds and
        /// priority lapses, Input.IsGamepadActive goes false, the interface drops
        /// out of focus navigation and the highlight disappears - then the next
        /// nudge of the stick brings it back. That flicker is what makes menu
        /// navigation feel broken rather than smooth, and there is no mouse being
        /// reached for inside a headset anyway.
        /// </summary>
        /// <summary>1 (default) makes the stick fly the deployment camera without
        /// holding the left trigger. 0 restores the vanilla modifier.</summary>
        private static readonly bool FlyDeployment =
            VrConfig.Bool("deployment_stick_fly", true);

        private static bool InDeployment()
        {
            try
            {
                Mission m = Mission.Current;
                return m != null && m.Mode == TaleWorlds.Core.MissionMode.Deployment;
            }
            catch
            {
                return false;
            }
        }

        private static bool InMenu()
        {
            try
            {
                return Mission.Current == null || VrMissionPhase.UiWantsScreen;
            }
            catch
            {
                return false;
            }
        }

        // The raw sticks, kept as they were read so the stick mouse can steer a
        // pointer with them without going back to the native input state and
        // getting a different frame's answer than the pad did.
        private static float _lStickX, _lStickY, _rStickX, _rStickY;
        private static bool _lStickHave, _rStickHave;

        /// <summary>
        /// This frame's deflection for one stick, deadzoned exactly as the pad
        /// deadzoned it. False when that controller was not tracking.
        /// </summary>
        public static bool TryGetStick(bool right, out float x, out float y)
        {
            x = right ? _rStickX : _lStickX;
            y = right ? _rStickY : _lStickY;
            return Active && (right ? _rStickHave : _lStickHave);
        }

        /// <summary>
        /// Reads the controllers and rebuilds the key table. Called once per frame
        /// from VrInputManager.UpdateKeyData.
        /// </summary>
        public static void Sample(float dt)
        {
            if (!Enabled || _broken)
                return;

            try
            {
                Array.Copy(Down, Was, KeyCount);
                Array.Clear(Down, 0, KeyCount);
                Array.Clear(Analog, 0, KeyCount);

                // Cleared before the hands are read. A left controller that stopped
                // reporting would otherwise leave the modifier stuck down, and
                // the right stick would stay a D-pad with no way to release it.
                _lThumbDown = false;

                _tracked = false;

                if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                    return;

                bool anyInput = false;

                // The calibration mode borrows the sticks and buttons to move the
                // weapon around, and a stick cannot both aim a menu cursor and
                // nudge a sword. While it is up the pad reports nothing at all -
                // which is also what stops the player walking off a cliff while
                // lining up a hilt.
                bool suppressed = VrHandCalibration.Capturing;

                if (input.Left.IsActive != 0)
                {
                    _tracked = true;
                    if (!suppressed)
                        anyInput |= Hand(input.Left, left: true);
                }

                if (input.Right.IsActive != 0)
                {
                    _tracked = true;
                    if (!suppressed)
                        anyInput |= Hand(input.Right, left: false);
                }

                // THE DEPLOYMENT CAMERA FLIES WITHOUT HOLDING ANYTHING.
                //
                // Not a workaround - a deliberate change to a vanilla rule that
                // does not survive contact with a headset. MissionScreen's own
                // free-camera branch reads:
                //
                //     bool num6 = !_isGamepadActive
                //              || Mission.Mode != MissionMode.Deployment
                //              || Input.IsKeyDown(InputKey.ControllerLTrigger);
                //     if (num6) { zero.x = GetGameKeyAxis("MovementAxisX"); ... }
                //
                // So on a pad, during deployment, the movement axis is only read
                // while the left trigger is HELD - the stick otherwise belongs to
                // troop selection. On a couch that is a sensible modifier. Here
                // it means the one phase where the player is flying a camera
                // around a battlefield is the one phase the stick appears dead.
                //
                // Reporting the trigger as held for the whole of deployment makes
                // the stick fly the camera outright. The cost is the selection
                // function that shared the stick, and during deployment there is
                // nothing else the trigger does - no combat to swing at.
                if (FlyDeployment && InDeployment())
                    Set(InputKey.ControllerLTrigger, true);

                // Held directions step a menu more than once. See ApplyRepeat.
                ApplyRepeat(Math.Max(dt, 0f));

                _idleSeconds = anyInput ? 0f : _idleSeconds + Math.Max(dt, 0f);

                if (_tracked && !_loggedFirst)
                {
                    _loggedFirst = true;
                    VrLog.Info("Gamepad: the controllers are reporting as an Xbox pad. Left "
                               + "stick moves, right stick is the right stick, triggers are "
                               + "the triggers, grips are the bumpers; A/B on the right hand "
                               + "and X/Y on the left are the four face buttons, and the left "
                               + "menu button is Start.");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Gamepad: reading the controllers failed; the pad is off for the "
                            + "rest of the session and the keyboard is unaffected.", ex);
            }
        }

        /// <summary>
        /// One controller's contribution. Returns true if the player is touching
        /// anything on it, which is what the mouse hand-off measures.
        /// </summary>
        private static bool Hand(BvrHandState h, bool left)
        {
            bool used = false;

            Vec2 stick = ApplyDeadzone(h.ThumbstickX, h.ThumbstickY);

            // Kept for the stick mouse. The deadzoned value, not the raw one, so
            // the pointer and the pad agree about when a stick is at rest.
            if (left) { _lStickX = stick.x; _lStickY = stick.y; _lStickHave = true; }
            else      { _rStickX = stick.x; _rStickY = stick.y; _rStickHave = true; }

            // The stick pressed in. Captured before anything reads it, because
            // the LEFT one is the D-pad modifier and the left hand is sampled
            // first - so by the time the right hand runs, this is already known.
            bool thumbDown = (h.Buttons & (int)BvrButton.ThumbClick) != 0;

            if (left)
                _lThumbDown = thumbDown;

            if (thumbDown)
            {
                Set(left ? InputKey.ControllerLThumb : InputKey.ControllerRThumb, true);
                used = true;
            }

            // THE D-PAD CHORD: hold the left stick in, push the right stick.
            //
            // A Touch or Sense controller has no D-pad, and the game wants one -
            // several screens bind ControllerLUp/Down/Left/Right and nothing
            // else. Everything else on the controller is already spoken for, so
            // the only way to reach four more directions is a chord, and the
            // stick click is the last spare modifier there is.
            //
            // The right stick was chosen over the left deliberately: the left one
            // is movement, and a chord that also walked you somewhere while you
            // pressed it would be its own problem.
            bool dpad = !left && DpadChord && _lThumbDown;

            // THE POINTER BORROWS A STICK TOO.
            //
            // Same rule as the chord, different borrower: while the stick mouse
            // is driving the cursor, this stick belongs to the cursor and to
            // nothing else. Reporting it as well meant a single push moved the
            // pointer, panned the campaign map behind the menu, AND scrolled the
            // list being pointed at - so the entry being aimed at slid out from
            // under the cursor as it arrived.
            //
            // Suppressed one frame later than the chord is, because the pointer
            // latches in the application tick while this runs from Input.Update.
            // That is one frame of stick at the moment the pointer takes hold and
            // none after it, which is the difference between a twitch and a mode.
            bool pointer = VrStickMouse.OwnsStick(left);

            InputKey stickKey = left ? InputKey.ControllerLStick : InputKey.ControllerRStick;

            // The analog axis is suppressed for the duration. It is the camera on
            // the right stick, and swinging the view while picking a direction
            // out of a menu is exactly what the chord exists to avoid.
            if (!dpad && !pointer && (stick.x != 0f || stick.y != 0f))
            {
                Analog[(int)stickKey] = stick;
                used = true;
            }

            if (dpad)
            {
                Set(InputKey.ControllerLLeft,  stick.x <= -DigitalThreshold);
                Set(InputKey.ControllerLRight, stick.x >= DigitalThreshold);
                Set(InputKey.ControllerLDown,  stick.y <= -DigitalThreshold);
                Set(InputKey.ControllerLUp,    stick.y >= DigitalThreshold);

                if (Math.Abs(stick.x) >= DigitalThreshold || Math.Abs(stick.y) >= DigitalThreshold)
                    used = true;

                LogDpadOnce();

                // The rest of this hand still runs - the triggers, the grip and
                // the face buttons keep working while the chord is held. Only the
                // stick itself is borrowed.
            }

            // The digital directions as well as the axis. Bannerlord binds BOTH -
            // "Up" is W or ControllerLStickUp, while "MovementAxisY" is the stick -
            // and which one a given screen reads is its business, not ours.
            //
            // These are the half that scrolled the page: a list reads
            // ControllerRStickDown as "next", not the analog axis, so suppressing
            // only the axis would have left the symptom exactly as reported.
            if (!dpad && !pointer)
            {
                Set(left ? InputKey.ControllerLStickLeft : InputKey.ControllerRStickLeft,
                    stick.x <= -DigitalThreshold);
                Set(left ? InputKey.ControllerLStickRight : InputKey.ControllerRStickRight,
                    stick.x >= DigitalThreshold);
                Set(left ? InputKey.ControllerLStickDown : InputKey.ControllerRStickDown,
                    stick.y <= -DigitalThreshold);
                Set(left ? InputKey.ControllerLStickUp : InputKey.ControllerRStickUp,
                    stick.y >= DigitalThreshold);
            }

            // Triggers keep their analog value: Bannerlord reads a trigger through
            // the same GetKeyState as a stick, so throwing the travel away here
            // would turn a squeeze into an on-off switch further down.
            InputKey triggerKey = left ? InputKey.ControllerLTrigger : InputKey.ControllerRTrigger;
            if (h.Trigger > 0.01f)
            {
                Analog[(int)triggerKey] = new Vec2(h.Trigger, 0f);
                used = true;
            }
            Set(triggerKey, h.Trigger >= TriggerThreshold);

            // THE TRIGGERS CLICK, but only where there is a pointer to click
            // with. A trigger during play is a swing or a shot, and binding it
            // to a mouse button there would fire both.
            if (VrStickMouse.Active && h.Trigger >= TriggerThreshold)
            {
                Set(left ? InputKey.RightMouseButton : InputKey.LeftMouseButton, true);
                used = true;
            }

            // Grip -> bumper. A grip is a squeeze and a bumper is a click, so this
            // one really is a threshold and not an axis.
            Set(left ? InputKey.ControllerLBumper : InputKey.ControllerRBumper,
                h.Grip >= GripThreshold);
            if (h.Grip >= GripThreshold)
                used = true;

            // The face buttons. Touch splits them across two controllers - X and Y
            // on the left, A and B on the right - and an Xbox pad has all four in
            // one cluster, which is the ControllerR* group. So both hands feed the
            // same four keys and nothing is lost.
            bool primary = (h.Buttons & (int)BvrButton.Primary) != 0;
            bool secondary = (h.Buttons & (int)BvrButton.Secondary) != 0;

            if (left)
            {
                Set(InputKey.ControllerRLeft, primary);    // X
                Set(InputKey.ControllerRUp, secondary);    // Y
            }
            else
            {
                Set(InputKey.ControllerRDown, primary);    // A
                Set(InputKey.ControllerRRight, secondary); // B
            }

            used |= primary || secondary;

            // Touch has one menu button, on the left hand. Start rather than Back,
            // because Start is what opens things and Back is what closes them -
            // and closing already has a face button.
            if ((h.Buttons & (int)BvrButton.Menu) != 0)
            {
                Set(InputKey.ControllerROption, true);
                used = true;
            }

            return used;
        }

        /// <summary>
        /// The eight stick directions, which are the keys a menu navigates with.
        ///
        /// Only these repeat. A held trigger or a held A button means something
        /// quite different from a held direction, and re-firing their edges would
        /// turn one swing into several.
        /// </summary>
        private static readonly InputKey[] RepeatKeys =
        {
            InputKey.ControllerLStickUp,   InputKey.ControllerLStickDown,
            InputKey.ControllerLStickLeft, InputKey.ControllerLStickRight,
            InputKey.ControllerRStickUp,   InputKey.ControllerRStickDown,
            InputKey.ControllerRStickLeft, InputKey.ControllerRStickRight,

            // The D-pad too. It is held and pushed exactly as a stick direction
            // is, and a menu it steps through one item per flick would be worse
            // for being a chord rather than better.
            InputKey.ControllerLUp,        InputKey.ControllerLDown,
            InputKey.ControllerLLeft,      InputKey.ControllerLRight,
        };

        /// <summary>True while the chord is borrowing the right stick. The stick
        /// mouse asks, because a pointer that slid across the screen while the
        /// player was picking a direction would fight the thing it is for.</summary>
        public static bool DpadActive => DpadChord && _lThumbDown && Active;

        /// <summary>0 gives the right stick back and drops the D-pad chord.</summary>
        private static readonly bool DpadChord = VrConfig.Bool("dpad_chord", true);

        /// <summary>The left stick pressed in, from the frame's own sample. The
        /// left hand is read first, so the right hand can rely on it.</summary>
        private static bool _lThumbDown;

        private static bool _loggedDpad;

        private static void LogDpadOnce()
        {
            if (_loggedDpad)
                return;

            _loggedDpad = true;
            VrLog.Info("D-pad: holding the LEFT stick in and pushing the RIGHT stick now "
                       + "sends the pad's D-pad, which several screens bind and nothing "
                       + "else on a VR controller can reach. The right stick's own axis "
                       + "and directions are suppressed while it is held, so the camera "
                       + "does not swing at the same time. Set dpad_chord = 0 to turn it "
                       + "off.");
        }

        /// <summary>Seconds a direction must be held before it starts repeating,
        /// and the interval between repeats after that. The usual keyboard feel:
        /// long enough that one step is one step, short enough that a long list
        /// is not a chore.</summary>
        private static readonly float RepeatDelay = VrConfig.Float("gamepad_repeat_delay", 0.40f);
        private static readonly float RepeatRate = VrConfig.Float("gamepad_repeat_rate", 0.12f);

        private static readonly float[] HeldFor = new float[KeyCount];
        private static readonly float[] NextRepeat = new float[KeyCount];

        /// <summary>
        /// True where a held direction means "next item" rather than "keep
        /// walking": outside a mission at all, or with a menu open inside one.
        /// </summary>
        /// <summary>
        /// Repeat belongs where menus are, and InMenu already answers that. The
        /// safe answer on failure is false: no repeat cannot make a horse bolt,
        /// and a spurious one can.
        /// </summary>
        private static bool RepeatWanted()
        {
            return InMenu();
        }

        /// <summary>
        /// Makes a held stick direction fire more than once.
        ///
        /// IsPressed is a true edge - Down AND NOT Was - which is right for a
        /// button and wrong for menu navigation: holding the stick up moved the
        /// selection exactly one row and then stopped, so a long list had to be
        /// flicked through one item at a time.
        ///
        /// Re-arming Was while the direction is still held presents the engine
        /// with a fresh edge, which is the same trick a keyboard's auto-repeat
        /// plays and reaches every consumer of IsKeyPressed without any of them
        /// needing to know.
        /// </summary>
        private static void ApplyRepeat(float dt)
        {
            // ONLY WHERE THERE IS A MENU, AND THE HORSE IS WHY.
            //
            // ControllerLStickUp is not just a menu key. It is bound as the
            // movement key too:
            //
            //     new GameKey(0, "Up", "Generic", InputKey.W, InputKey.ControllerLStickUp, ...)
            //
            // so re-arming its edge during play presents the engine with a fresh
            // PRESS of forward several times a second instead of one held key.
            // On foot that is survivable; mounted it is not, because a horse
            // reads a press of forward as "start running" - which is exactly the
            // reported spam, the animal trying to break into a run over and over
            // while the stick is simply held.
            //
            // Repeat is a menu-navigation convenience, so it applies where menus
            // are and nowhere else.
            if (!RepeatWanted())
            {
                Array.Clear(HeldFor, 0, KeyCount);
                return;
            }

            foreach (InputKey key in RepeatKeys)
            {
                int i = (int)key;
                if (i < 0 || i >= KeyCount)
                    continue;

                if (!Down[i])
                {
                    HeldFor[i] = 0f;
                    NextRepeat[i] = RepeatDelay;
                    continue;
                }

                HeldFor[i] += dt;

                if (HeldFor[i] < NextRepeat[i])
                    continue;

                // Was is what IsPressed compares against, so clearing it is
                // exactly "pretend this key was up last frame".
                Was[i] = false;
                NextRepeat[i] = HeldFor[i] + RepeatRate;
            }
        }

        private static void Set(InputKey key, bool down)
        {
            int i = (int)key;
            if (i < 0 || i >= KeyCount)
                return;

            if (down)
            {
                Down[i] = true;

                // A digital key with no analog value of its own still has to answer
                // GetKeyState, because that is how GameKey reads it.
                if (Analog[i].x == 0f && Analog[i].y == 0f)
                    Analog[i] = new Vec2(1f, 0f);
            }
        }

        /// <summary>
        /// A radial deadzone, rescaled so the usable range still reaches 1.
        ///
        /// Per-axis deadzones are the common shortcut and they make diagonals
        /// wrong: push exactly north-east at 0.2 on each axis and both are cut,
        /// so the stick reads dead in a direction it is plainly being held. The
        /// magnitude is the thing that is noisy, so the magnitude is what gets
        /// the treatment.
        /// </summary>
        private static Vec2 ApplyDeadzone(float x, float y)
        {
            float mag = (float)Math.Sqrt(x * x + y * y);
            if (mag <= Deadzone || mag <= 1e-5f)
                return Vec2.Zero;

            float scaled = Math.Min((mag - Deadzone) / (1f - Deadzone), 1f);
            return new Vec2(x / mag * scaled, y / mag * scaled);
        }

        // --- what the input manager asks -------------------------------------

        public static bool TryKeyState(InputKey key, out Vec2 value)
        {
            int i = (int)key;
            if (!Active || i < 0 || i >= KeyCount)
            {
                value = Vec2.Zero;
                return false;
            }

            value = Analog[i];
            return value.x != 0f || value.y != 0f;
        }

        public static bool IsDown(InputKey key)
        {
            int i = (int)key;
            return Active && i >= 0 && i < KeyCount && Down[i];
        }

        public static bool IsPressed(InputKey key)
        {
            int i = (int)key;
            return Active && i >= 0 && i < KeyCount && Down[i] && !Was[i];
        }

        public static bool IsReleased(InputKey key)
        {
            int i = (int)key;
            return Active && i >= 0 && i < KeyCount && !Down[i] && Was[i];
        }

        /// <summary>
        /// Stamps our keys into the engine's own 256-byte key table.
        ///
        /// Input.Update keeps that array and hands it to UpdateKeyData every frame;
        /// GetFirstKeyPressedInRange and the key-rebinding screens read it directly
        /// rather than going back through IsKeyDown. Writing into it is what makes
        /// a VR button bindable in the game's own controls menu instead of merely
        /// working in the places we thought of.
        /// </summary>
        public static void StampKeyData(byte[] keyData)
        {
            if (!Active || keyData == null)
                return;

            int n = Math.Min(keyData.Length, KeyCount);
            for (int i = 0; i < n; i++)
            {
                if (Down[i])
                    keyData[i] = 1;
            }
        }
    }
}
