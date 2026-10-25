using System;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Which stereo strategy is running.
    ///
    /// THE MODES, AND WHAT SEPARATES THEM
    ///
    /// The first two render ONE eye per DISPLAY frame through the engine's own
    /// view, and differ only in what becomes of the eye that was not drawn - so
    /// they share every view, camera and render target and can be swapped
    /// without leaving a battle.
    ///
    ///   Afr        The other eye is HELD - its own last real render, submitted
    ///              with the pose it was drawn at so the compositor reprojects
    ///              it. Exact geometry, one frame stale. The shipping path.
    ///
    ///   DepthReproject  (DR) The other eye is SYNTHESISED from the rendered
    ///              one through its depth buffer, so both eyes are one instant -
    ///              at the price of invented pixels wherever a near edge hid
    ///              something the rendered eye never saw.
    ///
    /// The third removes the halving those work around by rendering both eyes
    /// from ONE engine frame:
    ///
    ///   NativeStereo every view-dependent operation run twice, so the second eye
    ///                arrives lit and finished.
    ///
    /// FOUR MODES WERE REMOVED, AND WHY
    ///
    ///   Native  Two SceneViews, one per eye. It never rendered a whole scene -
    ///           agents came out as floating heads and missing bodies, because
    ///           rgl draws each mesh once a frame and the first view to ask
    ///           claims it - and in its last form it died on an access
    ///           violation about a second into every battle. Four attempts,
    ///           none of which produced a frame worth looking at.
    ///
    ///   Hybrid  DR with a held real render substituted while the head was
    ///           still. That is a setting's worth of difference wearing a
    ///           mode's clothes, and its cost - the second eye visibly changing
    ///           character as you start and stop moving - never justified a
    ///           third entry on the menu.
    ///
    ///   FullAfr Two game frames per display frame - one eye, the world held
    ///           still, the other eye - submitted as one pair. It asked the
    ///           engine for twice the frame rate and doubled the CPU's culling
    ///           and submission, and native stereo reaches the same thing (both
    ///           eyes real, one instant) from ONE engine frame. Removed
    ///           2026-09-17 once native stereo worked with DLSS.
    ///
    ///   DrawStereo  Every scene draw issued twice at the D3D level. Its second
    ///           eye came from the G-buffer - unlit, soft, sometimes missing the
    ///           ground - and native stereo does the same job completely.
    ///           Removed 2026-09-17. The native duplication path is still
    ///           compiled in vp_patch.cpp; managed holds it off every frame.
    ///
    /// All four are gone from the enum, the parser and the panel. A config
    /// naming any of them falls through to AFR rather than erroring.
    ///
    /// WHY THE SWITCH IS LATCHED TO THE NEXT MISSION
    ///
    /// Changing mode can mean creating or destroying SceneViews, and this repo
    /// has recorded what that costs: "doing view surgery mid-frame is what
    /// crashed the stereo path". OnSceneRenderingStarted is the one moment the
    /// engine is known to tolerate it, so the request is held until then.
    /// </summary>
    public static class VrRenderMode
    {
        public enum Mode
        {
            Afr = 0,

            /// <summary>AFR's renderer, but the second eye is synthesised from
            /// the first's depth instead of held. See VrDepthReproject.</summary>
            DepthReproject = 2,

            /// <summary>
            /// ONE engine frame, both eyes real and COMPLETE.
            ///
            /// Draw duplication proved the geometry half and stopped there: it
            /// re-issued the scene's draws for the second eye but nothing
            /// downstream of the G-buffer - no lighting, no sky, no post - so
            /// its second eye was a raw unlit channel.
            ///
            /// This duplicates by RULE rather than by pass. An operation is
            /// view-dependent when it binds constants carrying the main camera,
            /// or reads a surface an earlier view-dependent operation wrote;
            /// anything such an operation writes gets a twin of its own. That
            /// one rule carries the second eye all the way to the composite -
            /// and it leaves the shadow cascades single and shared, because
            /// their uploads carry LIGHT matrices and fail the camera test.
            ///
            /// The second eye's constants are the first eye's moved sideways:
            /// the runtime's two eye poses have identical orientation and one
            /// shared symmetric frustum, so the difference between them is a
            /// translation and nothing else. No rotation is applied anywhere,
            /// which is what separates this from every latch experiment before
            /// it.
            ///
            /// Cost: the GPU shades two eyes, which is what stereo is. The CPU
            /// pays one culling pass, one animation update and one set of shadow
            /// maps.
            /// </summary>
            NativeStereo = 6,

            /// <summary>
            /// DR and AFR together, chosen per frame by how far the head has
            /// travelled since the second eye was last really drawn.
            ///
            /// WHY THIS IS A MODE AND NOT A SETTING UNDER DR
            ///
            /// It already existed, as the native config key dr_still_afr - and
            /// that key defaulted to ON. So selecting DR in the menu ran the
            /// hybrid, the menu offered three modes of which two were the same
            /// one, and every judgement made about "DR" was really a judgement
            /// about the hybrid. Nothing in the panel or the log said so.
            ///
            /// The two halves fail in opposite directions, which is the whole
            /// case for combining them. A held real render is EXACT when nothing
            /// has moved - pixel for pixel what a fresh render would produce -
            /// so every warp artefact is pure loss in that case, and standing
            /// still looking at something is precisely when artefacts get
            /// examined. Once the head moves, the held eye is displaced by the
            /// movement and the warp, which is built from this instant, is
            /// right instead.
            ///
            /// What it costs is the changeover. The second eye changes character
            /// as the player starts and stops moving, and that is visible. It is
            /// a real trade rather than a free win, which is the other reason it
            /// belongs on the menu where it can be compared, rather than on by
            /// default where it cannot.
            /// </summary>
        }

        /// <summary>
        /// True for the modes that render through the ENGINE'S OWN scene view,
        /// one eye per frame. AFR and DR differ only in what becomes of the
        /// eye that was not rendered, so every piece of plumbing below the
        /// submit - the retarget, the camera binding, the fov hold - is shared
        /// and asks this rather than naming a mode.
        /// </summary>
        public static bool UsesAfrPipeline(Mode mode)
        {
            return mode == Mode.Afr || mode == Mode.DepthReproject || mode == Mode.NativeStereo;
        }

        /// <summary>Whether the whole pipeline runs twice per frame.</summary>
        public static bool NativeStereoActive => Active == Mode.NativeStereo;

        /// <summary>
        /// True for the modes that BUILD the second eye out of the first rather
        /// than rendering or holding it.
        ///
        /// WHY ANYTHING OUTSIDE THE WARP NEEDS TO KNOW
        ///
        /// These are the only modes that require the two eyes to share a
        /// frustum, and that requirement reaches all the way up into how the
        /// scene is projected. The warp shows the destination eye what the
        /// source eye saw; anything the source eye never saw cannot be
        /// synthesised, only invented.
        ///
        /// On a canted headset the runtime hands out MIRRORED asymmetric fovs -
        /// measured here, left eye L-52.6 R38.6 and right eye L-38.6 R52.6, with
        /// identical orientations. Each eye therefore holds about 709 pixels of
        /// its own outer field that the other eye has never seen, roughly a
        /// quarter of the width. Rendering each eye its true frustum is a clear
        /// win when both eyes are real - it is a third of the pixels back - and
        /// it makes DR impossible, because a quarter of every synthesised eye
        /// would have no source at all.
        ///
        /// So the frustum choice is not a preference to be set once. It follows
        /// the mode, and VrCameraDriver.SymmetricFov asks this.
        /// </summary>
        public static bool WarpsSecondEye(Mode mode)
        {
            return mode == Mode.DepthReproject;
        }

        /// <summary>What is actually running. Only ever changed at a scene
        /// boundary.</summary>
        public static Mode Active { get; private set; } = Parse(VrConfig.String("stereo_mode", "afr"));

        /// <summary>What the player has asked for. Applied at the next mission.</summary>
        public static Mode Requested { get; private set; } = Active;

        public static bool ChangePending => Requested != Active;

        /// <summary>
        /// In native mode the engine's own scene view is disabled, so only the two
        /// eye views draw. That is the whole experiment - see the class note - and
        /// the cost is that the monitor freezes on its last frame. Off is the
        /// escape hatch for anyone who wants to see both at once and judge for
        /// themselves.
        /// </summary>
        public static bool NativeDisablesEngineView { get; } =
            VrConfig.Bool("native_stereo_disable_engine_view", true);

        public static void Request(Mode mode)
        {
            if (mode == Requested)
                return;

            Requested = mode;

            if (mode == Active)
            {
                VrLog.Info("Render mode request cancelled; staying on " + Active + ".");
                return;
            }

            // AFR, DR and the one-frame stereo modes need no scene boundary.
            //
            // The latch exists because changing to or from native mode creates
            // and destroys SceneViews, and this repo has recorded what that
            // costs mid-frame. These share every view, every camera and every
            // render target; they differ in what the native capture does with
            // the eye that was not drawn.
            // Nothing is allocated, nothing is destroyed, so making the player
            // leave the battle to try it would be a restriction with no cause
            // behind it.
            if (UsesAfrPipeline(Active) && UsesAfrPipeline(mode))
            {
                Mode was = Active;
                Active = mode;
                VrLog.Info("Render mode " + was + " -> " + Active + ", immediately: "
                           + "AFR, DR and the stereo modes share every view and "
                           + "render target, so there is nothing to rebuild at a scene "
                           + "boundary.");
                return;
            }

            VrLog.Info("Render mode " + mode + " requested; it takes effect at the next "
                       + "mission. Switching views mid-frame is what crashed the stereo "
                       + "path before, so the change waits for a scene boundary.");
        }

        /// <summary>
        /// Called from the one place a SceneView may safely be created or
        /// destroyed. Returns true when the mode actually changed.
        /// </summary>
        public static bool ApplyAtSceneBoundary()
        {
            if (Requested == Active)
                return false;

            Mode was = Active;
            Active = Requested;
            VrLog.Info("Render mode " + was + " -> " + Active + ".");
            return true;
        }

        public static Mode Parse(string text)
        {
            string name = (text ?? string.Empty).Trim();

            if (string.Equals(name, "depth", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "depthreproject", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "depth_reproject", StringComparison.OrdinalIgnoreCase))
                return Mode.DepthReproject;
            if (string.Equals(name, "nativestereo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "native_stereo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "truestereo", StringComparison.OrdinalIgnoreCase))
                return Mode.NativeStereo;
            // "native", "hybrid", "fullafr", "drawstereo" and
            // "singleframe" fall through to AFR. All were removed - the
            // two-SceneView native mode never rendered a whole scene and crashed
            // on contact with a battle, the hybrid was DR with a held eye
            // substituted, full AFR asked the engine for twice the frame rate for
            // what native stereo gets from one frame, and draw stereo's second eye
            // was an unlit G-buffer that native stereo finishes properly. A config
            // naming any of them gets the stable path rather than an error.

            return Mode.Afr;
        }

        public static string Name(Mode mode)
        {
            switch (mode)
            {
                case Mode.DepthReproject:    return "depth";
                case Mode.NativeStereo: return "nativestereo";
                default:          return "afr";
            }
        }
    }
}
