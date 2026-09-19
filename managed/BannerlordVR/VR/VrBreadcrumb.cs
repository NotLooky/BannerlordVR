using System;
using BannerlordVR.Interop;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Says which piece of our managed code is in flight, so a crash inside the
    /// engine can be attributed or cleared.
    ///
    /// WHY THE NATIVE STACK SCAN WAS NOT ENOUGH
    ///
    /// The crash logger can already report whether BannerlordVR.Native.dll is on
    /// the faulting thread's stack, and on the mission-entry crash it is not.
    /// That clears our NATIVE code and nothing else: our Harmony patches are
    /// JIT-compiled, so on a stack they are bare addresses belonging to no module
    /// at all - which is precisely what every report of that crash has ended
    /// with, and it is indistinguishable from the game's own managed code.
    ///
    /// So the answer cannot come from the stack. It comes from here: the managed
    /// side stamps what it is about to do, and the crash logger prints the stamp
    /// with its age. A breadcrumb four hundred milliseconds old says our patches
    /// were not running when the engine faulted - which, for a crash that lands
    /// during scene loading while the main thread is inside the engine, is the
    /// answer worth having. A breadcrumb of zero names the patch that was.
    ///
    /// COST
    ///
    /// One P/Invoke storing two integers, at points that already run once a frame
    /// at most. It is deliberately NOT placed inside per-agent or per-bone loops:
    /// a breadcrumb that costs something is one that gets removed later, and then
    /// the next crash is unattributable again.
    /// </summary>
    public static class VrBreadcrumb
    {
        // Mirrors the BVR_CRUMB_* ids in bvr_api.h. Both change together.
        public const int Idle          = 0;
        public const int AppTick       = 1;
        public const int CameraPatch   = 2;
        public const int AfrTick       = 3;
        public const int MissionTick   = 4;
        public const int SceneRender   = 5;
        public const int StereoCreate  = 6;
        public const int WeaponHands   = 7;
        public const int BodyHide      = 8;
        public const int EngineAim     = 9;

        private static bool _broken;

        /// <summary>
        /// Stamps the crumb. Never throws: this exists to explain a crash, so it
        /// must not become one - and it is called from inside patches whose whole
        /// contract is that a VR failure drops to flat mode rather than taking
        /// the game with it.
        /// </summary>
        public static void Set(int id)
        {
            if (_broken)
                return;

            try
            {
                NativeMethods.bvr_breadcrumb(id);
            }
            catch (Exception)
            {
                // Once. A breadcrumb that logs its own failure every frame would
                // bury the log it exists to make readable.
                _broken = true;
            }
        }
    }
}
