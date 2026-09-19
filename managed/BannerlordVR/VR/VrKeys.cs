using System;
using System.Collections.Generic;
using TaleWorlds.InputSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Config-named keyboard keys.
    ///
    /// Every hotkey in this mod was a hardcoded InputKey field, which is fine
    /// until two of them collide with something the player has bound, and then it
    /// is a rebuild. The names are InputKey's own, so "Home", "F9" and "NumPad5"
    /// all work and the enum is the documentation.
    ///
    /// A bad name is reported once and then treated as "no key" rather than
    /// throwing: a mistyped hotkey should cost you that hotkey, not the session.
    /// </summary>
    public static class VrKeys
    {
        private static readonly Dictionary<string, InputKey> Resolved =
            new Dictionary<string, InputKey>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> Warned =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool Pressed(string name)
        {
            InputKey key;
            return TryResolve(name, out key) && Input.IsKeyPressed(key);
        }

        public static bool Down(string name)
        {
            InputKey key;
            return TryResolve(name, out key) && Input.IsKeyDown(key);
        }

        public static bool TryResolve(string name, out InputKey key)
        {
            key = InputKey.Invalid;

            if (string.IsNullOrEmpty(name))
                return false;

            if (Resolved.TryGetValue(name, out key))
                return key != InputKey.Invalid;

            bool ok;
            try
            {
                key = (InputKey)Enum.Parse(typeof(InputKey), name.Trim(), true);
                ok = Enum.IsDefined(typeof(InputKey), key);
            }
            catch (Exception)
            {
                ok = false;
            }

            if (!ok)
            {
                key = InputKey.Invalid;

                if (Warned.Add(name))
                {
                    VrLog.Warn("Hotkey '" + name + "' is not an InputKey name, so that "
                               + "shortcut is off. Use the spelling from TaleWorlds' own "
                               + "InputKey enum - 'Home', 'F9', 'NumPad5' and so on.");
                }
            }

            Resolved[name] = key;
            return ok;
        }
    }
}
