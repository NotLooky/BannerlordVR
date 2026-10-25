using System;
using BannerlordVR.Interop;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 591: the interface rasterised at ui_supersample_native x the window.
    ///
    /// "The 3d UI is soft": TEST 590 gave the world panel 1408 window pixels for
    /// 70% of the view, ~620 px per tangent unit against the headset's ~1000, and
    /// one window has no more to give. Gauntlet places every mesh in NDC from its
    /// own ScreenSize, so the same draws into a bigger target come out sharper with
    /// the layout untouched; native binds a dense target wherever the engine binds
    /// the backbuffer (ui_supersample.cpp). The shaders' one pixel-space test - the
    /// clip rectangle against SV_Position - is scaled here (UiSupersamplePatch).
    ///
    /// Only while the interface goes on the overlay: the flat screen reads the real
    /// backbuffer. If native reports the interface never reached the backbuffer,
    /// this stands down for the session.
    /// </summary>
    internal static class VrUiSupersample
    {
        private static readonly float Factor = Math.Min(4f, VrConfig.Float("ui_supersample_native", 2f));

        /// <summary>The clip-rect scale for this frame's meshes; 1 when off.</summary>
        public static float ClipScale { get; private set; } = 1f;

        private static float _sent = -1f;
        private static bool _failed;

        public static void Tick()
        {
            if (_failed)
                return;

            try
            {
                bool want = Factor > 1.01f && VrSystem.IsActive && VrUiSettings.Show
                            && VrMissionPhase.UiWantsOverlay;
                float k = want ? Factor : 0f;
                if (k != _sent)
                {
                    _sent = k;
                    NativeMethods.bvr_set_ui_supersample(k);
                    VrLog.Info(want
                        ? $"UI supersample (TEST 591): the interface is drawn at {k:0.##}x the window while it "
                          + "floats over the world."
                        : "UI supersample (TEST 591): off - the interface is drawn at the window's size.");
                }
                // 1 = drawing dense; 3 = standing aside while a 3D scene layer draws
                // into the window with depth; 2 = native gave up after ~3 s of no
                // interface in the backbuffer.
                int state = want ? NativeMethods.bvr_get_ui_supersample() : 0;
                ClipScale = state == 1 ? k : 1f;
                if (state == 2)
                    StandDown("the engine never drew the interface into the window's backbuffer");
            }
            catch (Exception ex)
            {
                StandDown(ex.Message);
            }
        }

        private static void StandDown(string why)
        {
            _failed = true;
            ClipScale = 1f;
            try
            {
                NativeMethods.bvr_set_ui_supersample(0f);
            }
            catch (Exception)
            {
                // The native side is gone; nothing is armed there either.
            }
            VrLog.Warn("UI supersample (TEST 591): standing down for this session - " + why
                       + ". The interface keeps the window's own resolution.");
        }
    }
}
