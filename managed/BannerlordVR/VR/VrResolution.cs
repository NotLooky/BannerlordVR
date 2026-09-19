using System;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Makes the resolution slider do something the moment it moves.
    ///
    /// WHY IT DID NOTHING BEFORE
    ///
    /// render_scale sized two things at startup: the OpenXR swapchains and the
    /// eye render targets. Both are allocated once, the compositor reads the
    /// swapchains continuously, and this repo has a long scar about what happens
    /// when views and targets are rebuilt mid-frame. So the slider saved the
    /// value, said "takes effect on the NEXT LAUNCH", and did exactly that -
    /// which is correct, and useless for a setting you tune by looking at it.
    ///
    /// WHAT CHANGED
    ///
    /// The engine can already render a scene at less than its target's size and
    /// upscale into it - AFW's shader notes record rgl doing exactly that, at
    /// half size, of its own accord - and SceneView.SetScale is the control for
    /// it. That resizes NOTHING: the target, the swapchain and every copy
    /// between them keep the dimensions they were allocated with. Only the
    /// number of pixels the scene is actually shaded at changes.
    ///
    /// So the slider now does both halves of the job:
    ///
    ///   immediately   the scene is shaded at the new fraction, which is the
    ///                 part you can see and the part that buys frame rate
    ///   next launch   the targets are allocated at that size too, which also
    ///                 buys the memory and the copy bandwidth
    ///
    /// THE ONE ASYMMETRY, AND IT IS HONEST
    ///
    /// Downwards works immediately. Upwards cannot exceed the target that was
    /// allocated at launch - there is no detail there to ask for - so raising
    /// the slider above the launch value is the one case that really does wait.
    /// It says so rather than pretending.
    /// </summary>
    public static class VrResolution
    {
        /// <summary>0 disables the live half and restores next-launch-only
        /// behaviour, in case SetScale misbehaves on some driver.</summary>
        private static readonly bool LiveEnabled = VrConfig.Bool("render_scale_live", true);

        /// <summary>
        /// What the targets were ACTUALLY allocated at. Read once, because that
        /// is what happened at startup and no later edit changes it.
        /// </summary>
        private static readonly float LaunchScale =
            Clamp(VrConfig.Float("render_scale", 0.9f), 0.1f, 2.0f);

        /// <summary>What the player is asking for now.</summary>
        public static float Wanted { get; private set; } = LaunchScale;

        /// <summary>
        /// The fraction of the allocated target to shade, so that the ABSOLUTE
        /// resolution comes out at Wanted.
        ///
        /// Wanted / LaunchScale rather than Wanted, because the target is
        /// already LaunchScale of the runtime's recommendation - shading
        /// Wanted OF THAT would compound the two and land at the product.
        ///
        /// Clamped at 1: the target has no more pixels than it was made with.
        /// </summary>
        public static float LiveFactor
        {
            get
            {
                if (!LiveEnabled || LaunchScale <= 0.01f)
                    return 1.0f;

                return Clamp(Wanted / LaunchScale, 0.25f, 1.0f);
            }
        }

        /// <summary>True when the request is above what the targets can serve,
        /// so the extra really is waiting for a relaunch.</summary>
        public static bool CappedByLaunchSize => LiveEnabled && Wanted > LaunchScale + 1e-3f;

        public static void SetWanted(float scale)
        {
            Wanted = Clamp(scale, 0.1f, 2.0f);
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
