using System;
using System.IO;
using System.Reflection;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR
{
    /// <summary>
    /// A PROBE, not a mode. It answers one question and then gets out of the way:
    /// can rgl be made to render the LIVE MISSION SCENE, from a camera of our
    /// choosing, into a texture of our choosing, WITHOUT a second SceneView?
    ///
    /// WHY THAT IS THE QUESTION
    ///
    /// Native stereo in this repo has always meant a second SceneView, and it has
    /// always failed the same way. The reason was finally pinned down in
    /// VrAgentVisibility: the visibility pass does not GATE the drawing of agents,
    /// it ENQUEUES them, once per frame, into something the first view to ask
    /// consumes. A second view was never going to find anything left. Turning that
    /// pass off does not help - it removes agents from the render list entirely,
    /// which is the same wall from the other side.
    ///
    /// WHAT THE CYBERPUNK PORT DID, AND WHY IT APPLIES HERE
    ///
    /// TEST 46 in the config abandoned this path on the strength of that project's
    /// own cp2077-native-simultaneous-exhausted.md - "exhausted at every explored
    /// lever". That document is dated 2026-06-09. Its path-A-M-A1-SUCCESS.md is
    /// dated 2026-06-24, and records the wall being cleared. The shipped mod now
    /// advertises "Real stereo, not reprojection". So the reasoning TEST 46 rests
    /// on is simply out of date.
    ///
    /// They did not fix the second view. They gave up on it permanently and used a
    /// DIFFERENT SUBSYSTEM: an entity carrying a render-to-texture camera
    /// component, which the engine drives through its own complete frame graph
    /// because it is an ordinary game object rather than a second claimant on the
    /// frame's one view. The registry was never made to yield.
    ///
    /// Bannerlord has the same split, and this repo has never tried the other
    /// half of it. SceneView is the registered-view path - the one that fails.
    /// The tableau system is the other one:
    ///
    ///   Texture.CreateTableauTexture(name, handler, obj, w, h)
    ///   TableauView.CreateTableauView(name) + SetContinuousRendering(true)
    ///   BannerlordTableauManager.RegisterCharacterTableauScene(scene, type)
    ///   BannerlordTableauManager.RequestCharacterTableauRender(
    ///       id, path, poseEntity, CAMERA, type)
    ///
    /// That last call takes a Scene registered separately and a Camera, and puts
    /// the result in a texture. It is a native "render this scene from this camera
    /// into this target" primitive that does not go through SceneView at all -
    /// structurally the same door the Cyberpunk port walked through.
    ///
    /// WHAT IS GENUINELY UNKNOWN, AND WHY THIS IS A PROBE
    ///
    /// The system is built for CHARACTER portraits: a posed figure in a small
    /// dedicated scene at a few hundred pixels. Three things could each kill it
    /// outright, and no amount of reading the API surface will say which:
    ///
    ///   1. It may refuse a live mission scene, or only ever draw the poseEntity
    ///      it is handed rather than the world around it.
    ///   2. It may be one-shot per request rather than per-frame, in which case
    ///      the cost of asking every frame is the whole question.
    ///   3. It may not scale to eye resolution.
    ///
    /// So this does not try to be half a stereo mode. It sets the machinery up,
    /// asks for one render of the mission scene from the RIGHT EYE's camera, and
    /// writes the result to a PNG. Looking at that file answers the question
    /// outright: if the battlefield is in it, the route is real and worth
    /// building on. If it is black, empty, or one floating soldier, we know which
    /// of the three killed it and we have not spent a mode finding out.
    ///
    /// It runs ALONGSIDE whatever stereo mode is active and never touches the
    /// display path, so a failure costs a log line rather than the headset.
    ///
    /// OFF by default, and it should stay off except when someone is reading the
    /// answer. RequestCharacterTableauRender is being handed arguments it was not
    /// written for - a null pose entity, a battle scene - and native code given
    /// unexpected arguments is entitled to crash rather than return an error.
    /// </summary>
    public static class VrTableauStereo
    {
        /// <summary>tableau_stereo = 1 to run the probe once at the start of the
        /// next mission.</summary>
        private static readonly bool Enabled = VrConfig.Bool("tableau_stereo", false);

        /// <summary>Which slot to register the mission scene in. The tableau
        /// system keeps an array of scenes and selects by this, so if 0 is
        /// already spoken for by the game's own character scenes, this is the
        /// knob that moves.</summary>
        private static readonly int TableauType = VrConfig.Int("tableau_stereo_type", 0);

        /// <summary>How many frames to let the render run before saving. A
        /// one-shot request may complete immediately; a continuous one needs a
        /// few frames to produce anything. Long enough to cover both.</summary>
        private static readonly int SettleFrames = VrConfig.Int("tableau_stereo_frames", 90);

        /// <summary>
        /// THE TWO HALVES OF THIS PROBE ARE NOT EQUALLY SAFE, AND THE SECOND ONE
        /// KILLED THE PROCESS.
        ///
        /// Everything up to and including creating the texture, the view and the
        /// binding is inert: it allocates our own objects and touches no engine
        /// state. It ran twice with no ill effect and it answered a real question
        /// - the tableau system allocates happily at 3400x3468, so it does scale
        /// to eye resolution.
        ///
        /// The render request is a different animal. With a null pose entity it
        /// threw a catchable NullReferenceException ninety times and did nothing.
        /// With a REAL pose entity the process died on the first call - no
        /// managed exception, and nothing for the access-violation handler to
        /// catch either, which is the signature of the engine deciding to
        /// terminate rather than of a pointer going astray.
        ///
        /// Two things plausibly explain it, and both say the call was made into a
        /// system that was not ready for it:
        ///
        ///   * InitializeCharacterTableauRenderSystem has probably never run. The
        ///     game calls it when a screen that needs portraits starts up, and a
        ///     battle is not one. Asking an uninitialised render system to render
        ///     is asking it to dereference its own null internals.
        ///
        ///   * The scene registration reported 5 -> 5. If that array is a fixed
        ///     five slots rather than a list, we did not ADD the mission scene to
        ///     it - we REPLACED whatever the game had in slot 0, and then asked
        ///     the character-portrait path to run against a battle scene.
        ///
        /// So the dangerous half is now behind its own switch, off by default.
        /// The button runs the safe half and reports what it finds, which is the
        /// part that has been producing findings anyway. Setting this to 1 is a
        /// deliberate act by someone who has read this paragraph.
        /// </summary>
        private static readonly bool AllowRenderRequest =
            VrConfig.Bool("tableau_stereo_request", false);

        private enum Stage
        {
            Idle,
            Failed,
            Done,
            Settling,
        }

        /// <summary>
        /// Set by the panel button. The config key arms the probe for the next
        /// mission; this runs it on demand, which is what makes it usable
        /// without a restart and an edit.
        /// </summary>
        private static bool _requested;
        private static bool _loggedNoPose;

        /// <summary>What the panel button shows: 0 idle, 1 running, 2 finished,
        /// 3 failed.</summary>
        public static int PanelState
        {
            get
            {
                switch (_stage)
                {
                    case Stage.Settling: return 1;
                    case Stage.Done:     return 2;
                    case Stage.Failed:   return 3;
                    default:             return 0;
                }
            }
        }

        /// <summary>
        /// The panel asking for a run. Re-arms a probe that has already finished,
        /// so the button works more than once a session - which matters, because
        /// the interesting comparisons here are between one battle and the next
        /// and between one tableau slot and another.
        /// </summary>
        public static void RequestRun()
        {
            _requested = true;

            // A finished or failed run is reset so the button is not dead after
            // the first click. A run in progress is left alone rather than
            // restarted under itself.
            if (_stage == Stage.Done || _stage == Stage.Failed)
            {
                _stage = Stage.Idle;
                _frames = _paints = _requests = _requestFailures = 0;
                _loggedNoPose = false;
            }

            VrLog.Info("Tableau probe: asked for from the VR panel.");
        }

        /// <summary>The entity the render is anchored to. See RequestRender for
        /// why this is required rather than optional.</summary>
        private static GameEntity PoseEntity()
        {
            try
            {
                return Mission.Current?.MainAgent?.AgentVisuals?.GetEntity();
            }
            catch
            {
                return null;
            }
        }

        private static Stage _stage = Stage.Idle;
        private static Scene _scene;
        private static Texture _target;
        private static TableauView _view;
        private static int _frames;
        private static int _paints;
        private static int _requests;
        private static int _requestFailures;

        /// <summary>Counts the engine asking us to paint. Non-zero is by itself a
        /// significant finding: it means the tableau system has accepted the
        /// texture and is driving it per frame, which is most of what this mode
        /// would need.</summary>
        private static void OnPaintNeeded(Texture sender, EventArgs e)
        {
            _paints++;
        }

        private static readonly MethodInfo RegisterSceneMethod =
            typeof(BannerlordTableauManager).GetMethod(
                "RegisterCharacterTableauScene",
                BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>
        /// Texture.SetTableauView is not public, so it goes through reflection
        /// like the scene registration above.
        ///
        /// Worth saying why it is reached for at all rather than skipped: it is
        /// what tells the ENGINE which view drives this texture. Without it the
        /// texture exists and the view exists and neither knows about the other,
        /// which is a state that looks like a working setup right up until
        /// nothing paints - and "nothing painted" is one of the four verdicts
        /// this probe has to be able to tell apart.
        /// </summary>
        private static readonly MethodInfo SetTableauViewMethod =
            typeof(Texture).GetMethod(
                "SetTableauView",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static void BindTableauView(Texture target, TableauView view)
        {
            if (SetTableauViewMethod == null)
            {
                VrLog.Warn("Tableau probe: Texture.SetTableauView is not on this build; "
                           + "the texture and the view cannot be introduced to each "
                           + "other. Continuing, because the render request may not "
                           + "need the pairing - but zero paint callbacks below should "
                           + "be read in that light.");
                return;
            }

            SetTableauViewMethod.Invoke(target, new object[] { view });
        }

        /// <summary>
        /// Called every frame from the camera patch, alongside the other
        /// per-frame VR work. Does nothing at all unless the probe is armed.
        /// </summary>
        public static void Tick(MissionScreen screen)
        {
            // Either arming path will do: tableau_stereo = 1 runs it at the start
            // of the next mission, the panel button runs it now. The button is
            // what makes this usable - the config route costs a restart and an
            // edit to answer a question that is asked by looking at one image.
            if (!Enabled && !_requested)
                return;

            if (_stage == Stage.Failed || _stage == Stage.Done)
                return;

            try
            {
                Scene scene = Mission.Current?.Scene;
                if (scene == null || screen == null)
                    return;

                if (_stage == Stage.Idle)
                {
                    if (!Begin(scene))
                        _stage = Stage.Failed;
                    return;
                }

                // Settling: keep asking, and count how many asks the engine
                // tolerated. A request that throws every frame is a different
                // answer from one that is accepted and quietly draws nothing.
                RequestRender();

                if (++_frames >= SettleFrames)
                    Finish();
            }
            catch (Exception ex)
            {
                _stage = Stage.Failed;
                VrLog.Error("Tableau probe: failed and is now off for the session. "
                            + "Nothing else is affected - this never touched the "
                            + "display path.", ex);
            }
        }

        /// <summary>
        /// Builds the machinery. Every step is reported separately, because WHICH
        /// one fails is the entire information this probe exists to produce.
        /// </summary>
        private static bool Begin(Scene scene)
        {
            _scene = scene;

            uint w = 0, h = 0;
            if (NativeMethodsSize(ref w, ref h) == false || w == 0 || h == 0)
            {
                VrLog.Warn("Tableau probe: no headset eye size yet; waiting.");
                return true;   // not a failure, just early
            }

            VrLog.Info(string.Format(
                "Tableau probe starting. Asking rgl to render the LIVE MISSION SCENE "
                + "from the right eye's camera into a {0}x{1} tableau texture - with no "
                + "second SceneView anywhere. This is the path the Cyberpunk port used "
                + "after giving up on a second registered view, and it is the half of "
                + "the split this repo has never tried.", w, h));

            // 1. The target. isTableau is the flag that puts a render target under
            //    the tableau system rather than the ordinary one.
            try
            {
                _target = Texture.CreateTableauTexture(
                    "bvr_tableau_eye",
                    OnPaintNeeded,
                    null,
                    (int)w,
                    (int)h);

                if (_target == null)
                {
                    VrLog.Warn("Tableau probe: CreateTableauTexture returned null. The "
                               + "tableau system will not give us a target at eye size - "
                               + "which may itself be the answer (unknown 3: it does not "
                               + "scale).");
                    return false;
                }

                VrLog.Info("Tableau probe: texture created.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Tableau probe: CreateTableauTexture threw. The target could "
                            + "not be made at all.", ex);
                return false;
            }

            // 2. The view, set to render every frame rather than once on demand.
            //    A portrait is painted when it changes; an eye has to be painted
            //    always, and this is the switch that says so.
            try
            {
                _view = TableauView.CreateTableauView("bvr_tableau_view");
                if (_view != null)
                {
                    _view.SetContinuousRendering(true);
                    _view.SetDeleteAfterRendering(false);
                    BindTableauView(_target, _view);
                    VrLog.Info("Tableau probe: view created, continuous rendering on, "
                               + "texture bound to it.");
                }
                else
                {
                    VrLog.Warn("Tableau probe: CreateTableauView returned null; carrying "
                               + "on without one, in case the render request does not "
                               + "need it.");
                }
            }
            catch (Exception ex)
            {
                // Not fatal on its own - the render request may not need the view.
                VrLog.Error("Tableau probe: the tableau view could not be set up. "
                            + "Continuing without it.", ex);
            }

            // 3. Hand the tableau system the MISSION scene. This is unknown 1, and
            //    the one most likely to be refused: the array it goes into is
            //    called TableauCharacterScenes and the game fills it with small
            //    dedicated scenes for portraits.
            try
            {
                int before = SceneSlotCount();
                int pending = PendingRequests();

                VrLog.Info(string.Format(
                    "Tableau probe: the system holds {0} registered scene(s) and has {1} "
                    + "pending request(s) before we touch anything. That scene count is "
                    + "the thing to watch - if it is a FIXED five rather than a list, "
                    + "registering into slot {2} does not add the mission scene, it "
                    + "REPLACES whatever the game had there.",
                    before, pending, TableauType));

                if (!AllowRenderRequest)
                {
                    VrLog.Info("Tableau probe: stopping here. The scene registration and "
                               + "the render request are both behind tableau_stereo_request "
                               + "= 1, because that pair is what killed the process last "
                               + "run - the request took the game down on its first call "
                               + "with no managed exception and nothing for the "
                               + "access-violation handler to catch. Everything above this "
                               + "line is inert and has already answered one of the three "
                               + "unknowns: the tableau system DOES allocate at full eye "
                               + "resolution.");

                    _stage = Stage.Settling;
                    _frames = 0;
                    return true;
                }

                if (RegisterSceneMethod == null)
                {
                    VrLog.Warn("Tableau probe: RegisterCharacterTableauScene is not on "
                               + "this build of the game. The scene cannot be registered "
                               + "and the render request has nothing to draw.");
                    return false;
                }

                // FIRST, and this is the lever that was missing when it crashed.
                //
                // The game calls this when a screen that needs portraits starts
                // up. A battle is not one, so it has most likely never run - and
                // asking an uninitialised render system to render is asking it to
                // dereference its own null internals, which is exactly the shape
                // of a process that dies with nothing to catch.
                try
                {
                    BannerlordTableauManager.InitializeCharacterTableauRenderSystem();
                    VrLog.Info("Tableau probe: character tableau render system "
                               + "initialised. Pending requests now "
                               + PendingRequests() + ".");
                }
                catch (Exception ex)
                {
                    VrLog.Error("Tableau probe: InitializeCharacterTableauRenderSystem "
                                + "threw. Not proceeding to the render request - that "
                                + "call is what took the process down, and doing it "
                                + "against a system that could not initialise is the "
                                + "same experiment with worse odds.", ex);
                    return false;
                }

                RegisterSceneMethod.Invoke(null, new object[] { _scene, TableauType });

                int after = SceneSlotCount();
                VrLog.Info(string.Format(
                    "Tableau probe: mission scene registered in slot {0}. Registered "
                    + "scene count {1} -> {2}. A count that did not move means either "
                    + "that the registration was ignored, or that it replaced a slot "
                    + "rather than adding one - and the second of those is a change to "
                    + "the GAME'S state, not just ours.",
                    TableauType, before, after));
            }
            catch (Exception ex)
            {
                VrLog.Error("Tableau probe: registering the mission scene threw. This is "
                            + "the most likely of the three unknowns to be fatal - the "
                            + "system may simply not accept a battle scene.", ex);
                return false;
            }

            _stage = Stage.Settling;
            _frames = 0;
            return true;
        }

        /// <summary>
        /// The request itself: draw the registered scene, from OUR camera, into
        /// the tableau.
        ///
        /// THE POSE ENTITY IS NOT OPTIONAL, WHICH THE FIRST RUN ESTABLISHED.
        ///
        /// It was passed as null the first time, on the reasoning that we want
        /// the world rather than a posed figure. All ninety calls came back with
        /// a NullReferenceException raised INSIDE
        /// BannerlordTableauManager.RequestCharacterTableauRender itself - not in
        /// our code, and before any render could be attempted. The system
        /// dereferences that argument unconditionally.
        ///
        /// So it gets a real entity now: the player's own. That is not a
        /// concession, it is the sharper form of the question. If the render
        /// comes back with the BATTLEFIELD around the player, the pose entity is
        /// merely a required anchor and the system will draw a whole scene - the
        /// route is open. If it comes back with the player alone on an empty
        /// background, the pose entity is the SUBJECT and this is a portrait
        /// renderer that cannot be talked into being a camera. One image still
        /// separates those two answers; only the null was in the way.
        /// </summary>
        private static void RequestRender()
        {
            // The call that killed the process. Nothing reaches it unless someone
            // has deliberately set tableau_stereo_request = 1 having read why.
            if (!AllowRenderRequest)
                return;

            Camera camera = VrCameraDriver.EyeCamera(1);
            if (camera == null)
                return;

            GameEntity pose = PoseEntity();
            if (pose == null)
            {
                if (!_loggedNoPose)
                {
                    _loggedNoPose = true;
                    VrLog.Warn("Tableau probe: no player entity to anchor the render to "
                               + "yet, so nothing can be asked for. This clears as soon "
                               + "as the main agent exists.");
                }
                return;
            }

            try
            {
                BannerlordTableauManager.RequestCharacterTableauRender(
                    0,                    // characterCodeId - nothing to look up
                    "bvr_tableau_eye",    // the target, by name
                    pose,                 // REQUIRED - see the note above
                    camera,               // OUR eye camera. The whole point.
                    TableauType);

                _requests++;
            }
            catch (Exception ex)
            {
                // Counted rather than logged per frame: at frame rate a throwing
                // call would bury the verdict under thousands of identical lines.
                if (_requestFailures++ == 0)
                    VrLog.Error("Tableau probe: RequestCharacterTableauRender threw on "
                                + "the first call. Subsequent failures are counted, not "
                                + "logged.", ex);
            }
        }

        /// <summary>
        /// Writes the answer to a file and says where it is. The PNG is the
        /// verdict - everything else here is only there to explain it.
        /// </summary>
        private static void Finish()
        {
            _stage = Stage.Done;

            string path = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(VrLog.Path) ?? ".", "bvr_tableau_probe.png");

            bool saved = false;
            try
            {
                _target.SaveToFile(path, false);
                saved = true;
            }
            catch (Exception ex)
            {
                VrLog.Error("Tableau probe: the texture could not be saved. The counts "
                            + "below are still the finding.", ex);
            }

            VrLog.Info(string.Format(
                "TABLEAU PROBE VERDICT after {0} frame(s): "
                + "{1} paint callback(s) from the engine, {2} render request(s) accepted, "
                + "{3} refused.{4}",
                _frames, _paints, _requests, _requestFailures,
                saved ? " Image written to " + path : " No image was written."));

            VrLog.Info("How to read it. The BATTLEFIELD from slightly right of your head "
                       + "means rgl will render the mission scene outside SceneView, the "
                       + "wall that stopped native stereo four times is not in this path, "
                       + "and real simultaneous stereo is buildable on it. ONE SOLDIER or "
                       + "an empty studio background means the system only ever draws the "
                       + "pose entity it is given, and it is a portrait renderer that "
                       + "cannot be talked into being a camera. BLACK with paint callbacks "
                       + "above zero means it is driving the texture but our scene "
                       + "registration was ignored. BLACK with zero paints means nothing "
                       + "was driven at all and the request is one-shot at best.");

            // Let go of everything. The probe has said what it has to say, and a
            // continuously-rendering tableau left bound to a mission scene is a
            // cost with no purpose once the answer is written down.
            try
            {
                if (_view != null)
                    _view.SetContinuousRendering(false);
            }
            catch (Exception ex)
            {
                VrLog.Error("Tableau probe: could not stand the view down.", ex);
            }

            _target = null;
            _view = null;
            _scene = null;
        }

        /// <summary>How many scenes the tableau system is holding. Read before and
        /// after registration, because a count that does not move is the
        /// difference between "accepted" and "silently ignored".</summary>
        private static int SceneSlotCount()
        {
            try
            {
                Scene[] scenes = BannerlordTableauManager.TableauCharacterScenes;
                return scenes == null ? -1 : scenes.Length;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// How many renders the tableau system is carrying. Read before anything
        /// is asked of it, because it is the cheapest available signal of whether
        /// the system is ALIVE - an uninitialised one is the leading suspect for
        /// the crash, and this is the one number that speaks to it without
        /// touching the call that did the killing.
        /// </summary>
        private static int PendingRequests()
        {
            try
            {
                return BannerlordTableauManager.GetNumberOfPendingTableauRequests();
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>The headset's eye size, which is what makes this a test of the
        /// real thing rather than of a portrait.</summary>
        private static bool NativeMethodsSize(ref uint w, ref uint h)
        {
            uint gw, gh;
            if (NativeMethods.bvr_get_recommended_size(out gw, out gh) != (int)BvrStatus.Ok)
                return false;

            w = gw;
            h = gh;
            return true;
        }

        /// <summary>Called when a mission ends, so a second battle re-runs the
        /// probe rather than reporting the first one's answer again.</summary>
        public static void Reset()
        {
            if (_stage == Stage.Settling)
                VrLog.Info("Tableau probe: the mission ended before the probe finished; "
                           + "it will start again in the next one.");

            if (_stage != Stage.Failed)
                _stage = Stage.Idle;

            _target = null;
            _view = null;
            _scene = null;
            _frames = _paints = _requests = _requestFailures = 0;
        }
    }
}
