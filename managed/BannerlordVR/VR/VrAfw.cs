using System;
using BannerlordVR.Interop;

namespace BannerlordVR.VR
{
    /// <summary>
    /// AFW - Alternate Frame Warping. The game-side half; the warp itself is a
    /// compute shader in the native layer.
    ///
    /// WHAT IT CHANGES, IN ONE SENTENCE
    ///
    /// Same single scene render per frame as AFR, but the eye that was not
    /// rendered is SYNTHESISED from the one that was - reprojected through its
    /// own depth buffer - instead of being held from the previous frame.
    ///
    /// WHY THAT IS WORTH A NEW MODE
    ///
    /// Every artefact of the last several rounds has the same root: under AFR
    /// the two eyes on screen were drawn a frame apart, so the soldiers had
    /// moved, the camera had turned, and the stereo baseline had stopped being
    /// the IPD. Three rounds went into making two moments agree - a pose
    /// correction for the turn, pairing eyes by simulation tick, then holding
    /// the camera still across a pair.
    ///
    /// AFW deletes the second moment instead of correcting it. Both eyes come
    /// from one render of one frame. They cannot disagree about where anything
    /// is, because there is only one of everything.
    ///
    /// It also gives back what pairing cost: no frame is spent waiting for a
    /// partner, so the world runs at the full frame rate again rather than half
    /// of it. sync_sequential is not needed in this mode and does nothing.
    ///
    /// WHAT IT COSTS INSTEAD
    ///
    /// Disocclusion. The synthesised eye can only show what the rendered eye
    /// saw, and the few pixels hidden behind the near edge of a close object are
    /// genuinely absent from the source. With a 63 mm baseline those slivers are
    /// narrow. Where the reprojection cannot find a source pixel it takes the
    /// one straight ahead, so the failure looks like a slightly soft edge rather
    /// than a hole - see the shader note in afw_warp.cpp.
    /// </summary>
    public static class VrAfw
    {
        /// <summary>
        /// Whether AFW may be offered. Read once: the native layer has to
        /// allocate its hold textures differently for AFW - typeless, with an
        /// unordered-access view - and they are allocated long before anyone
        /// touches the menu.
        /// </summary>
        public static bool Available { get; } = VrConfig.Bool("afw", true);

        /// <summary>Whether the warp runs at all - true for AFW and for the
        /// hybrid, which is AFW with a held real eye substituted while the head
        /// is still.</summary>
        public static bool Enabled =>
            Available && VrRenderMode.Active == VrRenderMode.Mode.Afw;

        /// <summary>
        /// The value handed to bvr_set_afw: 0 AFR, 1 AFW, 2 hybrid.
        ///
        /// One int rather than two calls, because the native side has to apply
        /// both halves of the answer between one frame and the next. Two
        /// separate setters could be observed half-applied, and the half that
        /// renders is AFW running with the hybrid's decision about whether the
        /// head is still.
        /// </summary>
        private static int NativeMode
        {
            get
            {
                if (!Enabled)
                    return 0;

                // 1 is plain AFW. The hybrid was mode 2 and is gone; the native
                // side still understands the value, nothing selects it now.
                return 1;
            }
        }

        private static bool _published;
        private static bool _failed;
        private static bool _warnedGeometry;

        /// <summary>
        /// Hands the native warp this frame's stereo geometry and tells it
        /// whether to run.
        ///
        /// Published every frame rather than on change, because all four numbers
        /// move: the world scale slider changes the baseline, the resolution
        /// slider changes the focal length, and the engine moves its own far
        /// plane between scenes. A stale focal length is not a subtle error -
        /// disparity scales with it directly, so the synthesised eye would sit
        /// at the wrong depth everywhere.
        /// </summary>
        public static void Publish()
        {
            if (_failed)
                return;

            try
            {
                // Published in EVERY mode, not only when AFW is running. The
                // reticle places itself using the same focal length and
                // baseline, and it is drawn in AFR too - so withholding the
                // geometry when AFW is off would leave the crosshair stuck at
                // infinity and refusing to fuse.
                if (!Geometry(out float focalPx, out float baseline,
                              out float zNear, out float zFar))
                    return;

                NativeMethods.bvr_set_afw(NativeMode, focalPx, baseline, zNear, zFar);
                _published = Enabled;
            }
            catch (Exception ex)
            {
                _failed = true;
                VrLog.Error("Could not publish AFW geometry; the mode stays off and "
                            + "AFR is unaffected.", ex);
            }
        }

        /// <summary>
        /// The four numbers the warp runs on.
        ///
        /// focalPx converts an angle to pixels, and it must be the focal length
        /// of the frustum the scene was ACTUALLY RENDERED with - the widened
        /// containing one while a warp mode is running, the runtime's true
        /// asymmetric one otherwise. VrCameraDriver.EyeFocalPx knows which is in
        /// force and handles both, which is why it is the source here rather
        /// than any arithmetic on a single horizontal angle.
        ///
        /// baseline is measured between the two eye frames rather than taken
        /// from the runtime's IPD, so the world scale is already folded in - the
        /// eye positions are scaled on the way through. Measuring it also means
        /// it cannot drift out of step with what was rendered.
        /// </summary>
        private static bool Geometry(out float focalPx, out float baseline,
                                     out float zNear, out float zFar)
        {
            focalPx = 0f;
            baseline = 0f;
            zNear = 0f;
            zFar = 0f;

            if (!VrCameraDriver.HaveEyeFrames)
                return false;

            uint width = VrSystem.RecommendedEyeWidth;
            if (width == 0)
                return false;

            VrCameraDriver.EyeProjectionTarget(0, out float hfov, out float aspect);
            if (hfov <= 0.01f || hfov >= 3.13f)
                return false;

            // Asked of the driver rather than derived from hfov here: the old
            // (width / 2) / tan(hfov / 2) is the symmetric form and quietly
            // misreports an off-axis frustum by a few percent. See EyeFocalPx.
            focalPx = VrCameraDriver.EyeFocalPx(0, width);
            if (focalPx <= 0f)
                return false;

            MatrixFrameDistance(out baseline);
            if (baseline <= 0.0001f)
            {
                if (!_warnedGeometry)
                {
                    _warnedGeometry = true;
                    VrLog.Warn("AFW: the two eye frames are at the same place, so there "
                               + "is no baseline to reproject across. Staying on AFR.");
                }
                return false;
            }

            zNear = VrCameraDriver.NearPlaneValue;
            zFar = VrCameraDriver.FarPlaneValue;

            return zNear > 0f && zFar > zNear;
        }

        private static void MatrixFrameDistance(out float metres)
        {
            var a = VrCameraDriver.EyeFrame(0).origin;
            var b = VrCameraDriver.EyeFrame(1).origin;

            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;

            metres = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
