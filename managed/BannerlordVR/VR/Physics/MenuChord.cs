using System.Diagnostics;
using System.Reflection;
using BannerlordVR.Interop;
using HarmonyLib;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR opens its VR panel when both hands squeeze for 2 s (both
    // triggers in 0.2.6, both grips before). With the physics that squeeze is
    // a grab, a fist or a neck cut held on an enemy, and the panel opened in
    // the middle of a fight. Both stick clicks held instead open it
    // (menu_chord_seconds, 1 s; 0 gives the squeeze back). End still does.
    [HarmonyPatch]
    internal static class MenuChord
    {
        public static readonly float Seconds = VrConfig.Float("menu_chord_seconds", 1f);
        private static long _since;
        private static bool _fired;

        private static MethodBase Target => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrMenu"), "TriggerHold");
        public static bool Prepare() => EnemyPhysicsSettings.Configured && (Seconds > 0 && Target != null);
        public static MethodBase TargetMethod() => Target;

        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (PhysicsInterop.GetInputState(out BvrInputState state) != (int)BvrStatus.Ok) return false;
            const int click = (int)BvrButton.ThumbClick;
            if (state.Left.IsActive == 0 || state.Right.IsActive == 0 || (state.Left.Buttons & click) == 0 || (state.Right.Buttons & click) == 0)
            {
                _since = 0; _fired = false;
                return false;
            }
            long now = Stopwatch.GetTimestamp();
            if (_since == 0) _since = now;
            if (!_fired && now - _since >= (long)(Seconds * Stopwatch.Frequency))
            {
                _fired = true;
                PhysicsInterop.ToggleOverlay();
                PhysicsInterop.Haptic(0, .5f, .08f);
                PhysicsInterop.Haptic(1, .5f, .08f);
                PhysicsLog.Info("VR panel: both stick clicks held " + Seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s - toggled.");
            }
            return false;
        }
    }
}
