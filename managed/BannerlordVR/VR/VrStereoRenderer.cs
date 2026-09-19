using System;
using BannerlordVR.Interop;
using TaleWorlds.Library;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Phase 4, step 1: the two eye render targets, and the correlation that
    /// gives the native layer their ID3D11Texture2D pointers.
    ///
    /// Texture.Pointer is an rgl-internal address, not a D3D resource, and
    /// decoding rgl's struct layout would break on every game patch. So instead
    /// of reading the pointer we bracket the allocation:
    ///
    ///     bvr_begin_texture_capture(eye, w, h)
    ///     Texture.CreateRenderTarget(...)
    ///     bvr_end_texture_capture(eye)
    ///
    /// and the native CreateTexture2D hook records whatever render target of
    /// exactly that size appeared in between. Deterministic, and it needs no
    /// knowledge of rgl at all.
    ///
    /// The eye targets are deliberately created at the runtime's recommended
    /// size, unscaled. CopyResource requires the source and destination to match
    /// in dimensions, so these have to agree with the OpenXR swapchain exactly.
    /// Resolution scaling is a SceneView setting, applied later, not a smaller
    /// texture here.
    /// </summary>
    public static class VrStereoRenderer
    {
        private enum Stage
        {
            Idle,       // nothing allocated yet
            Waiting,    // targets requested; waiting for rgl to actually create them
            Ready,
            Failed,
        }

        /// <summary>
        /// How long to wait for rgl's render thread to service the creation.
        /// Generous: this costs one status word per frame and the alternative is
        /// giving up on a queue that was merely busy.
        /// </summary>
        private const int PollFrameBudget = 900;

        private static readonly Texture[] Color = new Texture[2];
        private static readonly Texture[] Depth = new Texture[2];
        private static readonly SceneView[] Views = new SceneView[2];

        private static Stage _stage = Stage.Idle;
        private static int _pollFrames;
        private static Scene _boundScene;
        private static bool _loggedSceneViewFailure;

        // ---- bring-up switches ------------------------------------------------
        // The stereo views start in the most conservative configuration that can
        // possibly work, and each risky feature is re-enabled one at a time from
        // the environment. Bisecting a crash that takes a game restart per attempt
        // is miserable enough without needing a rebuild for each one too.
        //
        // In Documents\Mount and Blade II Bannerlord\Configs\BannerlordVR.cfg:
        //
        //   stereo = 0          no eye targets or SceneViews at all (control)
        //   eye_autodepth = 0   use our own D16 depth target instead
        //   eye_shadows = 1     shadows on
        //   eye_postfx = 1      postfx on
        //   eye_order = 5       render after the game's view instead of before
        private static readonly bool UseAutoDepth = VrConfig.Bool("eye_autodepth", true);
        private static readonly bool UseShadows = VrConfig.Bool("eye_shadows", false);
        private static readonly bool UsePostfx = VrConfig.Bool("eye_postfx", false);
        private static readonly int RenderOrder = VrConfig.Int("eye_order", -10);
        private static readonly bool PushCamerasEveryFrame = VrConfig.Bool("eye_pushcam", true);
        private static readonly bool StereoEnabled = VrConfig.Bool("stereo", true);
        private static readonly bool UseContour = VrConfig.Bool("eye_contour", false);
        private static readonly bool SceneOcclusion = VrConfig.Bool("scene_occlusion", true);
        private static readonly bool EngineViewEnabled = VrConfig.Bool("engine_view", true);

        /// <summary>Renders the RIGHT eye view first. A diagnostic, not a setting -
        /// see the note at SetRenderOrder.</summary>
        private static readonly bool OrderSwap = VrConfig.Bool("eye_order_swap", false);

        /// <summary>
        /// How many eye views to build (1 or 2), and whether to enable them.
        ///
        /// These split the remaining question in two. Tests 1-3 established that
        /// the SceneViews are what crash, but not whether the fault is in
        /// CREATING them or in RENDERING from them:
        ///
        ///   eye_enable = 0   views are built and configured but never enabled.
        ///                    Stable => construction is fine and the fault is in
        ///                    rendering. Crash => the fault is in construction,
        ///                    most likely doing it mid-frame from OnFrameTick.
        ///   eyes = 1         only the left view. Stable => rgl tolerates one
        ///                    extra view but not two.
        /// </summary>
        private static readonly int EyeCount = Math.Max(1, Math.Min(2, VrConfig.Int("eyes", 2)));
        private static readonly bool EnableViews = VrConfig.Bool("eye_enable", true);

        /// <summary>eye_init = frametick restores the old behaviour of building and
        /// enabling the views from OnFrameTick, which crashes. Kept only so the two
        /// can be compared.</summary>
        private static readonly bool InitAtFrameTick =
            string.Equals(VrConfig.String("eye_init", "scenestart"), "frametick",
                          StringComparison.OrdinalIgnoreCase);

        private static bool _loggedStereoDisabled;
        private static bool _loggedPushCameras;

        /// <summary>True when both eye textures exist and native holds a pointer
        /// to each. The copy path in the Present hook gates on this.</summary>
        public static bool TexturesReady { get; private set; }

        public static Texture ColorTarget(int eye) => Color[eye];
        public static Texture DepthTarget(int eye) => Depth[eye];

        /// <summary>
        /// Allocates once per process. Called from the mission camera hook, so it
        /// runs on the main thread with the engine device already live - the
        /// native hook cannot be installed before the first Present.
        /// </summary>
        /// <summary>
        /// Builds and enables the eye views at the engine's own scene-rendering
        /// entry point, called from a postfix on MissionScreen.OnSceneRenderingStarted.
        ///
        /// Enabling a SceneView from OnFrameTick - mid-frame, while rgl is very
        /// likely walking its own view list - crashes the renderer within a
        /// second, even for a single view. Building them there is fine; it is
        /// specifically the enable that is fatal. This is the lifecycle point the
        /// engine uses for exactly this purpose, and the one the plan specified
        /// before I took a shortcut.
        /// </summary>
        public static void OnSceneRenderingStarted(MissionScreen screen)
        {
            if (!StereoEnabled || InitAtFrameTick || VrAfrRenderer.Enabled)
                return;

            try
            {
                Scene scene = Mission.Current?.Scene;
                if (scene == null)
                {
                    VrLog.Warn("OnSceneRenderingStarted with no scene; eye views not built.");
                    return;
                }

                if (!TexturesReady)
                {
                    // The textures are allocated from OnApplicationTick, well before
                    // a mission starts, precisely so they are ready by now.
                    VrLog.Warn("OnSceneRenderingStarted before the eye textures were " +
                               "ready; eye views not built for this mission.");
                    return;
                }

                DisableSceneViews();

                if (CreateSceneViews(scene))
                {
                    _boundScene = scene;
                    ApplyEngineViewState(screen);
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("Building the eye views at scene start failed.", ex);
            }
        }

        public static void EnsureCreated()
        {
            // BVR_STEREO=0 is the control: no eye targets, no SceneViews, nothing
            // touching the engine's context. That is exactly the Phase 3
            // configuration, which ran for many minutes without a crash. If a run
            // with this set still crashes, the fault is not in the stereo path at
            // all and everything since has been chasing the wrong thing.
            // AFR still needs the eye TEXTURES - it renders the engine own view
            // into them - and only needs the extra SceneViews skipped. Bailing
            // out here entirely left AFR with nothing to draw into.
            if (!StereoEnabled && !VrAfrRenderer.Enabled)
            {
                if (!_loggedStereoDisabled)
                {
                    _loggedStereoDisabled = true;
                    VrLog.Info("stereo = 0: no eye targets, no SceneViews (control run).");
                }
                return;
            }

            try
            {
                if (_stage == Stage.Idle)
                    Allocate();
                else if (_stage == Stage.Waiting)
                    Poll();

                // AFR wants the textures and nothing else; every line below this
                // manages extra SceneViews, which is exactly what it must not have.
                if (VrAfrRenderer.Enabled)
                    return;

                // View management stays here only in the old frametick mode, kept
                // for comparison. By default the views are built from
                // OnSceneRenderingStarted instead - see that method.
                if (TexturesReady && InitAtFrameTick)
                    EnsureSceneViews();

                // The views belong to one scene. Pressing Ready ends the deployment
                // phase, and quitting ends the mission; either can replace or
                // destroy the scene under views that still reference it, and the
                // next thing we do is push cameras into them. Dropping them the
                // moment the scene stops matching is what keeps that from becoming
                // a crash - OnSceneRenderingStarted rebuilds them for the new one.
                if (_boundScene != null && !ReferenceEquals(Mission.Current?.Scene, _boundScene))
                {
                    VrLog.Info("Mission scene changed; dropping the eye views until it restarts.");
                    DisableSceneViews();
                    _boundScene = null;
                }

                // Re-pointing each view at its camera has to happen every frame in
                // either mode, since a SceneView appears to snapshot the camera
                // when it is given one rather than reading it per render.
                // _boundScene, not Views[0] - the view objects now outlive every scene,
                // so their existence no longer means they are live.
                if (PushCamerasEveryFrame && _boundScene != null)
                    PushCameras();
            }
            catch (Exception ex)
            {
                VrLog.Error("Eye target setup failed; staying on the flat-colour path.", ex);
                _stage = Stage.Failed;
                ReleaseInternal();
            }
        }

        private static void Allocate()
        {
            uint width = VrSystem.RecommendedEyeWidth;
            uint height = VrSystem.RecommendedEyeHeight;

            if (width == 0 || height == 0)
                return;     // stay Idle and retry; the size arrives with the session

            // One capture window covering BOTH eyes. Arming per eye would race:
            // rgl creates the textures on its own thread, so an eye 0 texture
            // arriving late would be recorded against eye 1.
            int armed = NativeMethods.bvr_begin_texture_capture(0, width, height);
            if (armed != (int)BvrStatus.Ok)
            {
                VrLog.Warn("bvr_begin_texture_capture returned " + armed + "; no stereo targets.");
                _stage = Stage.Failed;
                return;
            }

            VrLog.Info($"Requesting eye render targets at {width}x{height}.");

            for (int eye = 0; eye < 2; eye++)
            {
                Color[eye] = Texture.CreateRenderTarget(
                    "BVR_Eye_" + eye, (int)width, (int)height,
                    autoMipmaps: false, isTableau: false,
                    createUninitialized: false, always_valid: true);

                Depth[eye] = Texture.CreateDepthTarget("BVR_Depth_" + eye, (int)width, (int)height);

                if (Color[eye] == null || Depth[eye] == null)
                {
                    VrLog.Warn($"Texture creation returned null for eye {eye}.");
                    _stage = Stage.Failed;
                    ReleaseInternal();
                    return;
                }
            }

            _pollFrames = 0;
            _stage = Stage.Waiting;
        }

        /// <summary>
        /// Waits for rgl to actually service the creation. The managed Texture
        /// objects already exist at this point; what we are waiting for is the
        /// ID3D11Texture2D behind each one, which the engine allocates on its own
        /// render thread some frames later.
        /// </summary>
        private static void Poll()
        {
            bool left = NativeMethods.bvr_end_texture_capture(0) == (int)BvrStatus.Ok;
            bool right = NativeMethods.bvr_end_texture_capture(1) == (int)BvrStatus.Ok;

            if (left && right)
            {
                TexturesReady = true;
                _stage = Stage.Ready;
                VrLog.Info($"Both eye textures captured after {_pollFrames} frames.");
                return;
            }

            if (++_pollFrames < PollFrameBudget)
                return;

            _stage = Stage.Failed;
            VrLog.Warn(
                $"Gave up waiting for the eye textures after {_pollFrames} frames " +
                $"(left={left}, right={right}). BannerlordVR.Native.log lists every " +
                "CreateTexture2D the engine made while armed - if none match the eye " +
                "size, rgl is not allocating through ID3D11Device::CreateTexture2D.");
            ReleaseInternal();
        }

        /// <summary>
        /// One SceneView per eye, each rendering the mission scene from its own
        /// camera into its own target. This is what makes the stereo genuine
        /// rather than a reprojection: two independent engine renders, each with
        /// correct culling, shadows and postfx for that eye's frustum.
        ///
        /// The render-target API lives on the View base class, not on SceneView.
        /// </summary>
        private static void EnsureSceneViews()
        {
            Scene scene = Mission.Current?.Scene;

            if (scene == null)
            {
                // Between missions, or the scene is not up yet. Tear down rather
                // than leave views pointed at a scene that is going away.
                if (_boundScene != null)
                {
                    DisableSceneViews();
                    _boundScene = null;
                }
                return;
            }

            if (ReferenceEquals(scene, _boundScene) && Views[0] != null &&
                (EyeCount < 2 || Views[1] != null))
            {
                if (PushCamerasEveryFrame)
                    PushCameras();
                return;
            }

            DisableSceneViews();

            if (CreateSceneViews(scene))
            {
                _boundScene = scene;
            }
            else if (!_loggedSceneViewFailure)
            {
                // Once only. This runs every frame, and the interesting case is
                // the first failure, not the ten thousandth.
                _loggedSceneViewFailure = true;
                VrLog.Warn("Eye SceneViews could not be created; headset will stay black.");
            }
        }

        /// <summary>
        /// Re-points each SceneView at its eye camera. OFF by default.
        ///
        /// Calling SetCamera every frame took the crash from 55 seconds down to
        /// under one: rgl does real work per call and does not expect to be
        /// re-pointed at 90 Hz. The eye cameras are updated in place instead, and
        /// are now exclusively ours, so the views have no reason to be re-bound.
        /// Kept behind BVR_EYE_PUSHCAM purely as a bisecting tool.
        /// </summary>
        private static void PushCameras()
        {
            for (int eye = 0; eye < EyeCount; eye++)
            {
                Camera camera = VrCameraDriver.EyeCamera(eye);
                if (camera == null || Views[eye] == null)
                    continue;

                Views[eye].SetCamera(camera);
            }

            if (!_loggedPushCameras)
            {
                _loggedPushCameras = true;
                VrLog.Info("Re-pushing eye cameras to their views every frame.");
            }
        }


        /// <summary>
        /// Optionally switches OFF the engine's own scene view.
        ///
        /// The monitor shows the same broken agents as the headset - floating
        /// heads and hands, bodies missing, flickering frame to frame. Our extra
        /// views are not merely failing to see things, they are corrupting the
        /// engine's own render. That is the signature of a "draw each mesh once
        /// per frame" optimisation: with three views competing, every entity is
        /// claimed by whichever view asks first and the others get nothing.
        ///
        /// With engine_view = 0 only our two eye views remain. If the agents come
        /// back whole in the headset, that theory is confirmed - and the fix is to
        /// stop asking the engine to render the same scene more than once, by
        /// alternating a single view between the eyes instead.
        /// </summary>
        private static void ApplyEngineViewState(MissionScreen screen)
        {
            // Native stereo turns it off by default, because that is the entire
            // experiment: the note above names "stop asking the engine to render
            // the same scene more than once" as the untested fix, and going from
            // three competing views to two is how that gets tested. The config key
            // still wins if it has been set explicitly, so anyone who wants to
            // watch both at once can.
            // Native stereo was the only mode that switched the engine's own view
            // off, and it is gone. Nothing forces it now; the config key is the
            // whole of the decision.
            bool wantEngineView = EngineViewEnabled;

            if (screen == null)
                return;

            // Symmetric, and it has to be. Switching back to AFR after a native
            // run leaves the engine's own view switched off, and AFR renders
            // THROUGH that view - so without the restore the headset goes black on
            // the way back and the toggle looks like it broke the working mode.
            if (wantEngineView && !_engineViewDisabled)
                return;

            try
            {
                SceneView engineView = screen.SceneView;
                if (engineView == null)
                {
                    VrLog.Warn("No engine SceneView to " + (wantEngineView ? "restore." : "disable."));
                    return;
                }

                engineView.SetEnable(wantEngineView);
                _engineViewDisabled = !wantEngineView;

                VrLog.Info(wantEngineView
                    ? "Engine's own SceneView re-enabled; the monitor is live again."
                    : "Engine's own SceneView DISABLED. The monitor will freeze on its "
                      + "last frame; only the two eye views render now, which is the "
                      + "whole point of native stereo - three views competing for the "
                      + "same meshes is what broke this path the first time.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not change the engine's SceneView state.", ex);
            }
        }

        /// <summary>Tracks whether WE switched the engine's view off, so it is only
        /// ever restored by the side that disabled it.</summary>
        private static bool _engineViewDisabled;

        private static bool _warnedOneEye;

        // --- the engine's camera still decides what exists ----------------------
        //
        // THE SAME LESSON AFR LEARNED, AND FOR THE SAME REASON.
        //
        // Native stereo draws through two views of our own, so it is tempting to
        // think the engine's CombatCamera no longer matters. It does. The engine
        // decides what to submit - visible sets, LOD, streaming, and occlusion when
        // it is on - from its OWN camera, and our views can only draw what survived
        // that. Leave CombatCamera pointing wherever the mouse left it and the
        // headset shows characters near the middle of the map and empty ground
        // everywhere else, which is exactly the report.
        //
        // AFR fixed this by aiming CombatCamera at the eye it was about to render
        // and doing it from the TAIL of the engine's own camera update, because
        // anything written earlier in the frame is overwritten when the engine
        // reaches UpdateCamera. Native stereo needs the identical fix; the only
        // difference is where to aim.
        //
        // Aimed at the HEAD, not at either eye. Both eyes are drawn this frame, so
        // there is no "current eye" to favour, and the midpoint is the pose whose
        // frustum contains both of theirs. The frustum handed over is eye 0's,
        // which under symmetric_fov is identical to eye 1's - so it covers both by
        // construction rather than by luck.
        private static int _cullAimTick;
        private static int _cullAims;
        private static int _cullFovWrites;

        /// <summary>
        /// Points the engine's own camera at the head, so its culling follows the
        /// player. Called from the postfix of MissionScreen.UpdateCamera - the last
        /// write of the frame, and the only one that survives.
        /// </summary>
        public static void AimEngineCullingCamera(MissionScreen screen)
        {
            // Only native stereo ever needed this, and native stereo is gone. Kept
            // as a no-op rather than deleted: the seam it uses - UpdateCamera's
            // tail - is hard-won knowledge and the method documents it.
            return;

#pragma warning disable 0162
            if (screen == null)
                return;

            if (!VrCameraDriver.HasPose)
                return;

            Camera combat = screen.CombatCamera;
            if (combat == null)
                return;

            try
            {
                MatrixFrame head = VrCameraDriver.HeadFrame;

                Vec3 target = new Vec3(
                    head.origin.x + head.rotation.f.x,
                    head.origin.y + head.rotation.f.y,
                    head.origin.z + head.rotation.f.z, -1f);

                combat.LookAt(head.origin, target, head.rotation.u);

                // The culling cone has to be at least as wide as the headset's, or
                // the edges of the view are culled away before either eye draws
                // them. Written only when it has actually drifted: rgl does real
                // work inside a frustum change and this file twice records doing it
                // blindly at frame rate as fatal.
                VrCameraDriver.EyeProjectionTarget(0, out float wantFov, out float wantAspect);

                if (Math.Abs(combat.HorizontalFov - wantFov) > 0.005f ||
                    Math.Abs(combat.GetAspectRatio() - wantAspect) > 0.005f)
                {
                    VrCameraDriver.ApplyEyeProjectionTo(combat, 0);
                    _cullFovWrites++;
                }

                _cullAims++;

                if (++_cullAimTick >= 150)
                {
                    _cullAimTick = 0;
                    VrLog.Info($"Native stereo: engine culling camera aimed at the head "
                               + $"{_cullAims} time(s), frustum corrected {_cullFovWrites}. "
                               + "This is what decides whether characters exist at all "
                               + "outside the middle of the map.");
                    _cullAims = 0;
                    _cullFovWrites = 0;
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("Native stereo: aiming the engine culling camera failed.", ex);
            }
        }

        /// <summary>
        /// Hands the engine its own scene view back. Called when AFR takes over, so
        /// a native run that disabled it cannot leave the mode it hands control to
        /// with nothing to render into.
        /// </summary>
        public static void RestoreEngineView(MissionScreen screen)
        {
            if (!_engineViewDisabled || screen == null)
                return;

            try
            {
                SceneView engineView = screen.SceneView;
                if (engineView == null)
                    return;

                engineView.SetEnable(true);
                _engineViewDisabled = false;
                VrLog.Info("Engine's own SceneView restored for AFR.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not restore the engine's SceneView.", ex);
            }
        }

        private static bool CreateSceneViews(Scene scene)
        {
            // Said out loud, because staying quiet about it cost a headset run.
            //
            // `eyes` defaulted to 2 but the config carried `eyes = 1`, left over
            // from bisecting the old stereo crash and annotated "not used in AFR
            // mode" - true right up until native stereo started reading it again.
            // One SceneView was built, the right eye was never drawn, and the
            // symptom in the headset was "native stereo is only left eye", which
            // reads like a rendering fault and is nothing of the kind.
            //
            // A mode that cannot do the one thing it exists for should say so.
            if (false && EyeCount < 2 && !_warnedOneEye)
            {
                _warnedOneEye = true;
                VrLog.Warn("Native stereo is running with eyes = " + EyeCount +
                           ". Only the left eye will ever be drawn - the right one "
                           + "stays black. Set eyes = 2 in BannerlordVR.cfg.");
            }

            for (int eye = 0; eye < EyeCount; eye++)
            {
                if (VrCameraDriver.EyeCamera(eye) == null)
                    return false;

                // Created ONCE for the life of the process and rebound to each new
                // scene. SceneView has CreateSceneView and no destroy, release or
                // dispose of any kind - so building a fresh pair per mission left
                // the old pair alive inside rgl, still referencing a scene that had
                // been torn down. The second mission then crashed while building
                // its views. Reuse is not an optimisation here; it is the only way
                // to not leak.
                SceneView view = Views[eye];
                if (view == null)
                {
                    view = SceneView.CreateSceneView();
                    if (view == null)
                        return false;

                    VrLog.Info($"Eye {eye} SceneView created (once per process).");
                }

                view.SetScene(scene);
                view.SetCamera(VrCameraDriver.EyeCamera(eye));
                view.SetRenderTarget(Color[eye]);

                if (UseAutoDepth)
                {
                    // Let the engine allocate its own depth buffer for this view.
                    //
                    // Texture.CreateDepthTarget produces DXGI_FORMAT_D16_UNORM -
                    // 16 bit, no stencil - and forcing a full scene render with
                    // shadows and postfx onto a stencil-less buffer is the leading
                    // suspect for the crash that appears within seconds of these
                    // views going live. The engine knows what its own passes need.
                    view.SetAutoDepthTargetCreation(true);
                }
                else
                {
                    view.SetAutoDepthTargetCreation(false);
                    view.SetDepthTarget(Depth[eye]);
                }

                // THE EXPERIMENT THAT NAMES THE MECHANISM, if the visibility
                // override does not fix it. Characters draw in the left eye - the
                // one that renders FIRST. Two causes produce that and they need
                // opposite fixes:
                //
                //   the FIRST view to render consumes them -> swapping moves the
                //       characters to the RIGHT eye
                //   the right eye view or camera is broken -> swapping changes
                //       nothing and they stay in the left
                //
                // eye_order_swap = 1 runs it, and one battle settles which of the
                // two we are even allowed to try to fix. Static, not per-frame:
                // rgl does real work inside these calls.
                view.SetRenderOrder(RenderOrder + (OrderSwap ? (1 - eye) : eye));
                view.SetSceneUsesSkybox(true);
                view.SetSceneUsesShadows(UseShadows);
                view.SetRenderWithPostfx(UsePostfx);

                if (UsePostfx)
                {
                    // The engine's own initialiser for the postfx parameter block.
                    // Turning postfx on without it leaves those params at whatever
                    // a fresh view starts with, which is the kind of unset pointer
                    // rgl's worker thread just died on.
                    view.SetPostfxFromConfig();
                }

                // Contour is the agent outline/highlight pass, and it is per-agent
                // per-view state - exactly the shape of thing an engine sizes for
                // its own view count. Agents flicker with our extra views alive,
                // and rgl access-violates at a fixed offset once a battle starts;
                // both point the same way. Off unless asked for.
                view.SetSceneUsesContour(UseContour);

                if (UseShadows)
                {
                    // Shadow and pointlight passes are per-view, so stereo pays
                    // for them twice. Halving their resolution is far cheaper than
                    // halving eye resolution and costs much less image quality.
                    view.SetShadowmapResolutionMultiplier(0.5f);
                    view.SetPointlightResolutionMultiplier(0.5f);
                }

                if (EnableViews)
                    view.SetEnable(true);

                Views[eye] = view;
            }

            // Occlusion culling is computed once per frame from the PRIMARY view.
            // Our eye views inherit that result, so anything the engine's own flat
            // camera cannot see is discarded as occluded before we draw - which is
            // why characters and props render near the middle of the map, where the
            // flat camera is looking, and vanish once you leave it.
            //
            // Turning it off costs draw calls but makes every view cull for itself.
            try
            {
                scene.SetOcclusionMode(SceneOcclusion);
                VrLog.Info($"Scene occlusion mode set to {SceneOcclusion}.");
            }
            catch (Exception ex)
            {
                VrLog.Error("SetOcclusionMode failed.", ex);
            }

            _loggedSceneViewFailure = false;
            VrLog.Info(
                $"Eye SceneViews built: count={EyeCount}, enabled={EnableViews}, " +
                $"autodepth={UseAutoDepth}, shadows={UseShadows}, postfx={UsePostfx}, " +
                $"order={RenderOrder} (swap={OrderSwap}), occlusion={SceneOcclusion}.");
            return true;
        }

        /// <summary>
        /// Stops the eye views rendering, but KEEPS the objects.
        ///
        /// There is no way to destroy a SceneView - the engine exposes only
        /// CreateSceneView - so dropping the reference does not remove it from
        /// rgl, it just loses our handle on something that still renders. The
        /// same pair is rebound to the next scene instead.
        /// </summary>
        private static void DisableSceneViews()
        {
            for (int eye = 0; eye < 2; eye++)
            {
                if (Views[eye] == null)
                    continue;

                try
                {
                    Views[eye].SetEnable(false);
                }
                catch (Exception ex)
                {
                    VrLog.Error($"Disabling the SceneView for eye {eye} threw.", ex);
                }
            }
        }

        /// <summary>Releases the eye targets. Only on shutdown: the native side
        /// holds an AddRef on each texture, so churning them mid-session would
        /// leave it pointing at resources nothing else references.</summary>
        public static void Release()
        {
            ReleaseInternal();
            _stage = Stage.Idle;
        }

        private static void ReleaseInternal()
        {
            TexturesReady = false;

            // Views first: the engine must stop rendering into the targets before
            // they are released.
            DisableSceneViews();
            _boundScene = null;

            for (int eye = 0; eye < 2; eye++)
            {
                TryRelease(ref Color[eye], "colour", eye);
                TryRelease(ref Depth[eye], "depth", eye);
            }
        }

        private static void TryRelease(ref Texture texture, string what, int eye)
        {
            if (texture == null)
                return;

            try
            {
                if (!texture.IsReleased)
                    texture.Release();
            }
            catch (Exception ex)
            {
                VrLog.Error($"Releasing the {what} target for eye {eye} threw.", ex);
            }

            texture = null;
        }
    }
}
