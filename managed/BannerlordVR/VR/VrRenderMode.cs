using System;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Which stereo strategy is running.
    ///
    /// THE FOUR, AND WHAT SEPARATES THEM
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
    ///   Afw        The other eye is SYNTHESISED from the rendered one through
    ///              its depth buffer, so both eyes are one instant - at the
    ///              price of invented pixels wherever a near edge hid something
    ///              the rendered eye never saw.
    ///
    /// The other two remove the halving those work around, by different means:
    ///
    ///   FullAfr    TWO renders per display frame - one eye, the world held
    ///              still, the other eye - submitted as one pair. Nothing is
    ///              held or warped. It costs the engine twice the frame rate.
    ///
    ///   DrawStereo ONE render, with every scene draw ISSUED TWICE at the D3D
    ///              level, once per eye. One game tick against full AFR's two.
    ///              Experimental: the second eye is assembled from the engine's
    ///              own G-buffer and is softer than the first.
    ///
    /// TWO MODES WERE REMOVED, AND WHY
    ///
    ///   Native  Two SceneViews, one per eye. It never rendered a whole scene -
    ///           agents came out as floating heads and missing bodies, because
    ///           rgl draws each mesh once a frame and the first view to ask
    ///           claims it - and in its last form it died on an access
    ///           violation about a second into every battle. Four attempts,
    ///           none of which produced a frame worth looking at.
    ///
    ///   Hybrid  AFW with a held real render substituted while the head was
    ///           still. That is a setting's worth of difference wearing a
    ///           mode's clothes, and its cost - the second eye visibly changing
    ///           character as you start and stop moving - never justified a
    ///           third entry on the menu.
    ///
    /// Both are gone from the enum, the parser and the panel. A config naming
    /// either falls through to AFR rather than erroring.
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
            /// the first's depth instead of held. See VrAfw.</summary>
            Afw = 2,

            /// <summary>
            /// AFR with the halving taken out: TWO game frames inside one
            /// display frame, so both eyes are real renders of one instant at
            /// the full refresh rate.
            ///
            /// WHAT THE OTHER THREE ARE ACTUALLY WORKING AROUND
            ///
            /// Every mode above renders one eye per DISPLAY frame. That is the
            /// root of all of it: each eye is refreshed at half the headset's
            /// rate, and the three modes differ only in how they cover for the
            /// eye that was not drawn - hold it and accept it is a frame stale,
            /// warp it and accept invented pixels, or switch between the two and
            /// accept the changeover. None of them removes the halving, because
            /// none of them can: there is one engine render per frame and two
            /// eyes to fill.
            ///
            /// Full AFR removes it by rendering twice per display frame. Eye 0,
            /// the world held still (VrSyncSequential's dt = 0), eye 1, submit
            /// the pair. Both halves are true engine renders, of the same
            /// simulation tick, from the same located head pose - so there is
            /// nothing stale to reproject, nothing synthesised to disocclude,
            /// and no changeover to notice. The artefact these three trade
            /// between simply is not produced.
            ///
            /// AND IT STILL ONLY EVER RUNS ONE VIEW PER FRAME
            ///
            /// Which is what separates it from Native. The engine draws each
            /// mesh once per frame and the first view to ask claims it - that is
            /// the measured fact that broke native stereo into floating heads.
            /// Full AFR never has two views live; it alternates ACROSS frames
            /// exactly as AFR does. It reaches simultaneous stereo without ever
            /// asking rgl for the thing rgl cannot do.
            ///
            /// WHAT IT COSTS, AND WHY IT IS NOT WHAT IT LOOKS LIKE
            ///
            /// At 72 Hz the engine has to produce 144 frames a second. The GPU
            /// cost is NOT doubled against native stereo - 144 one-eye renders
            /// is the same pixel count as 72 two-eye ones, and this mode also
            /// gets each eye its true asymmetric frustum back, because
            /// WarpsSecondEye is false and the frusta need not match. What
            /// doubles is CPU: culling and draw submission happen twice, and
            /// Bannerlord's main thread in a five-hundred agent battle is
            /// already the bottleneck. The second frame of a pair is the cheaper
            /// one - the world is frozen, so no AI, pathfinding or animation
            /// advances - but it is not free.
            ///
            /// So this is expected to be excellent wherever there is CPU
            /// headroom and to fall short of the display rate in a large field
            /// battle. It degrades the way any VR title does: xrWaitFrame stops
            /// blocking, pairs land on later display periods, and the compositor
            /// reprojects. The native log reports pairs per second, which is
            /// directly the per-eye refresh rate - the one number worth reading
            /// about this mode.
            /// </summary>
            FullAfr = 4,

            /// <summary>
            /// ONE engine frame, both eyes real. The scene is drawn once and
            /// every scene draw is ISSUED A SECOND TIME with the other eye's
            /// matrices, into render targets of our own.
            ///
            /// WHY THIS IS ITS OWN MODE AND NOT A SWITCH UNDER AFR
            ///
            /// It was a switch under AFR first, and that was a mistake worth
            /// recording. Turning it on pinned the engine's camera to one eye,
            /// took over the second eye's staging, and changed what AFR itself
            /// did - so the mode people actually play could not be trusted while
            /// the experiment existed. A path this young must not be able to
            /// alter a stable one by being enabled.
            ///
            /// As a mode of its own it is inert unless selected. AFR, AFW and
            /// the hybrid behave exactly as they did before it was written.
            ///
            /// WHAT IT COSTS AND WHAT IT BUYS
            ///
            /// One game tick, one animation update, one culling pass and one set
            /// of shadow cascades - against full AFR's two of each. The GPU
            /// still shades two eyes, which is the irreducible price of stereo,
            /// but the CPU work full AFR duplicates is done once.
            ///
            /// WHAT IS NOT FINISHED
            ///
            /// The second eye is assembled from the engine's own G-buffer
            /// surfaces, so it is sharp only to the upscaler's internal
            /// resolution and it inherits whatever that pass had done by the
            /// time it is read. Experimental, and named so on the panel.
            /// </summary>
            DrawStereo = 5,

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
            /// maps - not two, as full AFR does.
            /// </summary>
            NativeStereo = 6,

            /// <summary>
            /// AFW and AFR together, chosen per frame by how far the head has
            /// travelled since the second eye was last really drawn.
            ///
            /// WHY THIS IS A MODE AND NOT A SETTING UNDER AFW
            ///
            /// It already existed, as the native config key afw_still_afr - and
            /// that key defaulted to ON. So selecting AFW in the menu ran the
            /// hybrid, the menu offered three modes of which two were the same
            /// one, and every judgement made about "AFW" was really a judgement
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
        /// one eye per frame. AFR and AFW differ only in what becomes of the
        /// eye that was not rendered, so every piece of plumbing below the
        /// submit - the retarget, the camera binding, the fov hold - is shared
        /// and asks this rather than naming a mode.
        /// </summary>
        public static bool UsesAfrPipeline(Mode mode)
        {
            return mode == Mode.Afr || mode == Mode.Afw ||
                   mode == Mode.FullAfr || mode == Mode.DrawStereo ||
                   mode == Mode.NativeStereo;
        }

        /// <summary>
        /// True for the mode that renders TWO game frames per display frame.
        ///
        /// Asked by VrSyncSequential, which the mode depends on absolutely - a
        /// pair that is not one simulation tick is just AFR with extra frames -
        /// and by VrAfrRenderer, which publishes it to the native layer so the
        /// first Present of a pair knows to hand the begun frame back rather
        /// than ending it. See the note on the member.
        /// </summary>
        public static bool RendersPairPerDisplayFrame(Mode mode)
        {
            return mode == Mode.FullAfr;
        }

        /// <summary>Whether full AFR is the strategy currently running.</summary>
        public static bool FullAfrActive => Active == Mode.FullAfr;

        /// <summary>
        /// True for the mode that draws the scene once and issues every scene
        /// draw a second time for the other eye.
        ///
        /// Asked by VrAfrRenderer, which stops alternating in this mode because
        /// there is nothing to alternate - the engine draws one eye every frame
        /// and the duplicate supplies the other - and published to the native
        /// layer, which owns the duplication itself.
        /// </summary>
        public static bool DrawStereoActive => Active == Mode.DrawStereo;

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
        /// it makes AFW impossible, because a quarter of every synthesised eye
        /// would have no source at all.
        ///
        /// So the frustum choice is not a preference to be set once. It follows
        /// the mode, and VrCameraDriver.SymmetricFov asks this.
        /// </summary>
        public static bool WarpsSecondEye(Mode mode)
        {
            return mode == Mode.Afw;
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

            // AFR, full AFR, AFW and the hybrid need no scene boundary at all.
            //
            // The latch exists because changing to or from native mode creates
            // and destroys SceneViews, and this repo has recorded what that
            // costs mid-frame. These four share every view, every camera and
            // every render target; they differ in what the native capture does
            // with the eye that was not drawn, and - for full AFR - in whether
            // the XR frame ends on this render or waits for the next one.
            // Nothing is allocated, nothing is destroyed, so making the player
            // leave the battle to try it would be a restriction with no cause
            // behind it.
            //
            // Leaving full AFR mid-pair is safe for a reason worth naming: the
            // deferred XR frame is already begun and already published back as
            // prepared, which is exactly the state an ordinary Present consumes
            // and submits. The half-built pair goes up as a normal AFR frame and
            // nothing is left open. See xr_set_full_afr.
            if (UsesAfrPipeline(Active) && UsesAfrPipeline(mode))
            {
                Mode was = Active;
                Active = mode;
                VrLog.Info("Render mode " + was + " -> " + Active + ", immediately: "
                           + "AFR, full AFR, AFW and the hybrid share every view and "
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

            if (string.Equals(name, "afw", StringComparison.OrdinalIgnoreCase))
                return Mode.Afw;
            // "fullafr" is the canonical spelling; the other two are what a
            // person actually types, and a config edited by hand should work.
            if (string.Equals(name, "fullafr", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "full_afr", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "full afr", StringComparison.OrdinalIgnoreCase))
                return Mode.FullAfr;
            if (string.Equals(name, "drawstereo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "singleframe", StringComparison.OrdinalIgnoreCase))
                return Mode.DrawStereo;
            if (string.Equals(name, "nativestereo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "native_stereo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "truestereo", StringComparison.OrdinalIgnoreCase))
                return Mode.NativeStereo;
            // "native", "hybrid" and "afw+afr" fall through to AFR. Both modes were
            // removed - native stereo never rendered a whole scene and crashed on
            // contact with a battle, and the hybrid was AFW with a held eye
            // substituted, which is a setting's worth of difference dressed as a
            // mode. A config naming either now gets the stable path rather than an
            // error.

            return Mode.Afr;
        }

        public static string Name(Mode mode)
        {
            switch (mode)
            {
                case Mode.Afw:    return "afw";
                case Mode.FullAfr: return "fullafr";
                case Mode.DrawStereo: return "drawstereo";
                case Mode.NativeStereo: return "nativestereo";
                default:          return "afr";
            }
        }
    }
}
