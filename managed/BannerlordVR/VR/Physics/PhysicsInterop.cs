using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BannerlordVR.Interop;

namespace BannerlordVR.VR.Physics
{
    // The physics module runs beside an unmodified BannerlordVR release.
    // Controller input goes through the release's own wrapper (trigger/grip
    // swap, toggle grips) by reflection, falling back to the raw export; the
    // haptics and overlay calls hit the release's already loaded native DLL.
    internal static class PhysicsInterop
    {
        private delegate int InputWrapper(out BvrInputState state, bool battleGrip);
        private static InputWrapper _input;
        private static bool _resolved;

        public static int GetInputState(out BvrInputState state)
        {
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    Type native = typeof(BvrInputState).Assembly.GetType("BannerlordVR.Interop.NativeMethods");
                    MethodInfo method = native?.GetMethod("bvr_get_input_state", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new[] { typeof(BvrInputState).MakeByRefType(), typeof(bool) }, null);
                    if (method != null) _input = (InputWrapper)Delegate.CreateDelegate(typeof(InputWrapper), method);
                }
                catch (Exception ex) { PhysicsLog.Error("BannerlordVR input wrapper unavailable; using the raw native input.", ex); }
                PhysicsLog.Info("Controller input: physical squeeze and index trigger from the raw native state; BannerlordVR swap setting=" + SwapTriggerGrip() + ".");
            }
            // The raw export reports the PHYSICAL controls: Grip is the squeeze,
            // Trigger the index trigger. The release's wrapper swaps them into
            // its own convention (controls_swap_trigger_grip, where "Trigger"
            // means the holding squeeze) and latches toggle grips, which made
            // the physics read the held squeeze as a closed fist and never see
            // a grab. The physics wants the physical controls, unswapped.
            return bvr_get_input_state(out state);
        }

        private static PropertyInfo _swap;
        private static bool SwapTriggerGrip()
        {
            try
            {
                if (_swap == null)
                    _swap = typeof(BvrInputState).Assembly.GetType("BannerlordVR.Interop.NativeMethods")?
                        .GetProperty("SwapTriggerGrip", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                return _swap != null && (bool)_swap.GetValue(null);
            }
            catch { return false; }
        }

        public static void Haptic(int hand, float amplitude, float seconds)
        {
            try
            {
                bvr_trigger_haptic(hand, amplitude, seconds);
                MirrorTwoHanded(hand, amplitude, seconds);
            }
            catch (Exception) { }
        }

        // A weapon held in both hands is felt in both: the second hand only
        // ever had the pulses its own grabs made.
        public static void MirrorTwoHanded(int hand, float amplitude, float seconds)
        {
            if (FastAccess.TwoHandActive?.Invoke() != true) return;
            VrHand? gripping = FastAccess.TwoHandGripping?.Invoke();
            int other = gripping == VrHand.Left ? 0 : gripping == VrHand.Right ? 1 : -1;
            if (other >= 0 && other != hand) bvr_trigger_haptic(other, amplitude, seconds);
        }

        // BannerlordVR's own pulses (blocks, parries, clashes, its strikes)
        // reach the weapon hand only.
        [HarmonyLib.HarmonyPatch]
        internal static class OfficialPulsePatch
        {
            public static System.Reflection.MethodBase TargetMethod() =>
                HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat"), "Pulse");
            public static bool Prepare() => EnemyPhysicsSettings.Configured && (TargetMethod() != null);
            [HarmonyLib.HarmonyPostfix]
            public static void Postfix(int controller, float amplitude, float ms)
            {
                try { MirrorTwoHanded(controller, Math.Min(1f, Math.Max(0f, amplitude)), ms / 1000f); }
                catch (Exception) { }
            }
        }

        public static bool OverlayVisible()
        {
            try { return bvr_overlay_visible() != 0; }
            catch (Exception) { return false; }
        }

        // The ragdoll bridge DLL sits next to this assembly; DllImport alone
        // would search the game's folders, not the module's.
        public static bool LoadPhysicsNative()
        {
            try
            {
                string dir = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
                string path = Path.Combine(dir, "BannerlordVR.Physics.Native.dll");
                if (!File.Exists(path)) { PhysicsLog.Info("Physics native bridge missing at " + path); return false; }
                if (LoadLibraryW(path) == IntPtr.Zero)
                { PhysicsLog.Info("Physics native bridge LoadLibrary failed, win32 error " + Marshal.GetLastWin32Error()); return false; }
                return true;
            }
            catch (Exception ex) { PhysicsLog.Error("Physics native bridge could not load.", ex); return false; }
        }

        [DllImport("BannerlordVR.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int bvr_get_input_state(out BvrInputState state);
        [DllImport("BannerlordVR.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int bvr_trigger_haptic(int hand, float amplitude, float durationSeconds);
        [DllImport("BannerlordVR.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int bvr_overlay_visible();
        [DllImport("BannerlordVR.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int bvr_overlay_toggle();
        public static void ToggleOverlay()
        {
            try { bvr_overlay_toggle(); } catch (Exception) { }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
    }
}
