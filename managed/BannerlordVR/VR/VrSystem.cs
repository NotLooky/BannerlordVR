using System;
using BannerlordVR.Interop;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Owns the VR lifecycle and answers one question for the rest of the mod:
    /// is VR actually running right now?
    ///
    /// Hard rule: if anything here fails, the game must still launch and play
    /// flat. Every consumer checks <see cref="IsActive"/> before doing anything
    /// VR-specific. A missing headset, a missing runtime, or a missing native DLL
    /// are all normal states, not errors.
    /// </summary>
    public static class VrSystem
    {
        /// <summary>True once the native layer is loaded and initialized.
        /// Stereo rendering additionally requires a live session (Phase 2).</summary>
        public static bool IsActive { get; private set; }

        public static string StatusText { get; private set; } = "uninitialized";

        public static uint RecommendedEyeWidth { get; private set; }
        public static uint RecommendedEyeHeight { get; private set; }

        public static void Initialize()
        {
            try
            {
                if (!NativeBridge.TryLoad())
                {
                    StatusText = "flat mode - " + NativeBridge.LastError;
                    VrLog.Warn("Native layer unavailable, running flat. " + NativeBridge.LastError);
                    return;
                }

                VrLog.Info("Native layer loaded, version " + NativeBridge.NativeVersion);

                int rc = NativeMethods.bvr_init();
                if (rc != (int)BvrStatus.Ok)
                {
                    StatusText = "flat mode - bvr_init returned " + Describe(rc);
                    VrLog.Warn("bvr_init failed: " + Describe(rc));
                    return;
                }

                if (NativeMethods.bvr_get_recommended_size(out uint w, out uint h) == (int)BvrStatus.Ok)
                {
                    RecommendedEyeWidth = w;
                    RecommendedEyeHeight = h;
                    VrLog.Info($"Runtime recommends {w}x{h} per eye.");
                }

                // Records intent only. The session is actually created on the
                // render thread inside the Present hook, because that is the first
                // moment the engine's D3D11 device exists. SESSION_NOT_READY here
                // is the expected answer, not a failure.
                int sessionRc = NativeMethods.bvr_create_session();
                VrLog.Info("Session requested: " + Describe(sessionRc));

                IsActive = true;
                StatusText = "VR active (" + NativeBridge.NativeVersion + ")";
                VrLog.Info("VR system initialized.");
            }
            catch (Exception ex)
            {
                IsActive = false;
                StatusText = "flat mode - exception during init";
                VrLog.Error("VrSystem.Initialize threw; continuing in flat mode.", ex);
            }
        }

        private const int WarnAfterTicks = 600;   // ~10 s at 60 fps
        private static int _ticksWaitingForSession;

        /// <summary>Current OpenXR session state, as last reported by the runtime.</summary>
        public static XrSessionState SessionState { get; private set; } = XrSessionState.Unknown;

        /// <summary>True once the compositor is actually showing our frames.
        /// Everything stereo gates on this, not on <see cref="IsActive"/>.</summary>
        public static bool IsStereoReady =>
            SessionState == XrSessionState.Visible || SessionState == XrSessionState.Focused;

        /// <summary>
        /// Called once per frame from SubModule.OnApplicationTick. Cheap: the
        /// render thread pumps the real event queue inside the Present hook, so
        /// this only reads the published state.
        /// </summary>
        public static void Tick()
        {
            if (!IsActive)
                return;

            try
            {
                if (NativeMethods.bvr_poll_events(out int raw) != (int)BvrStatus.Ok)
                    return;

                var state = (XrSessionState)raw;

                // The session is created on the render thread from inside the
                // Present hook. If the state never leaves Unknown, that hook is
                // not firing - most likely because the engine presents through
                // IDXGISwapChain1::Present1 rather than Present. Without this
                // check the symptom is an indefinite silent wait, which is a
                // miserable thing to debug from scratch.
                if (state == XrSessionState.Unknown)
                {
                    _ticksWaitingForSession++;
                    if (_ticksWaitingForSession == WarnAfterTicks)
                    {
                        VrLog.Warn(
                            "No OpenXR session after ~" + WarnAfterTicks + " frames. The Present hook " +
                            "has probably not fired. Check BannerlordVR.Native.log: if it shows the hook " +
                            "installed but no device capture, the engine is using Present1 and d3d_hooks " +
                            "needs to hook IDXGISwapChain1 vtable index 22 as well.");
                    }
                    return;
                }

                if (state == SessionState)
                    return;

                XrSessionState previous = SessionState;
                SessionState = state;
                VrLog.Info($"Session state: {previous} -> {state}");

                if (state == XrSessionState.Focused && RecommendedEyeWidth == 0)
                {
                    // Size only becomes meaningful once the session exists.
                    if (NativeMethods.bvr_get_recommended_size(out uint w, out uint h) == (int)BvrStatus.Ok)
                    {
                        RecommendedEyeWidth = w;
                        RecommendedEyeHeight = h;
                    }
                }

                StatusText = "VR " + state;
            }
            catch (Exception ex)
            {
                VrLog.Error("VrSystem.Tick threw; disabling VR for this run.", ex);
                IsActive = false;
            }
        }

        public static void Shutdown()
        {
            if (!IsActive)
                return;

            try
            {
                // Release the engine cameras and eye targets before the native
                // layer goes away; afterwards nothing is driving them and the
                // native side still holds an AddRef on each texture.
                // Order matters: the SceneViews reference the eye cameras, so they
                // must stop rendering before the cameras are freed.
                VrStereoRenderer.Release();
                VrAfrRenderer.Reset();
                VrCameraDriver.Reset();
                VrCameraDriver.ReleaseCameras();

                NativeMethods.bvr_shutdown();
                VrLog.Info("VR system shut down.");
            }
            catch (Exception ex)
            {
                VrLog.Error("bvr_shutdown threw.", ex);
            }
            finally
            {
                IsActive = false;
                StatusText = "shut down";
            }
        }

        private static string Describe(int status)
        {
            return Enum.IsDefined(typeof(BvrStatus), status)
                ? ((BvrStatus)status).ToString()
                : "code " + status;
        }
    }
}
