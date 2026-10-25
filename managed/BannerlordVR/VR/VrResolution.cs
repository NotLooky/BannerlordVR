using System;
using BannerlordVR.Interop;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

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
    /// upscale into it - DR's shader notes record rgl doing exactly that, at
    /// half size, of its own accord - and SceneView.SetScale is the control for
    /// it. That resizes NOTHING: the target, the swapchain and every copy
    /// between them keep the dimensions they were allocated with. Only the
    /// number of pixels the scene is actually shaded at changes.
    ///
    /// So the slider does the first half of the job on its own:
    ///
    ///   as it moves    the scene is shaded at the new fraction, which is the
    ///                  part you can see and the part that buys frame rate
    ///
    /// THE ASYMMETRY THAT USED TO BE THE END OF IT
    ///
    /// Downwards works that way and upwards cannot: there is no detail in a
    /// target that was allocated smaller, so raising the slider above what the
    /// targets hold asks for pixels that were never rendered. That was a
    /// next-launch setting, and a next-launch setting is useless for something
    /// judged by looking at it.
    ///
    /// AND WHAT APPLY DOES ABOUT IT
    ///
    /// Reallocates the pair, live: the eye textures, the swapchains, and every
    /// derived texture sized from them. See the long note further down for the
    /// order that makes it safe, and the longer one above rescale_step in
    /// xr_context.cpp for what each half of the mod owns. The view goes black
    /// for a moment while it happens, because for those frames there genuinely
    /// is no eye image to show.
    /// </summary>
    public static class VrResolution
    {
        /// <summary>0 disables the live half and restores next-launch-only
        /// behaviour, in case SetScale misbehaves on some driver.</summary>
        private static readonly bool LiveEnabled = VrConfig.Bool("render_scale_live", true);

        /// <summary>
        /// What the targets are ACTUALLY allocated at right now.
        ///
        /// Starts at the config value, because that is what startup built them
        /// from - and STOPS being the config value the moment Apply rebuilds
        /// them, which is why this is no longer readonly.
        ///
        /// Getting that wrong is not cosmetic. LiveFactor divides by this to work
        /// out what fraction of the target to shade, so a stale value after a
        /// rebuild DOWNWARD would apply the reduction twice: rebuild 0.8 -> 0.5,
        /// and a divisor still reading 0.8 asks for 0.625 of a target that is
        /// already exactly right, landing at 0.31 of the runtime's size. The
        /// picture would come back soft and the number would say 0.50.
        /// </summary>
        private static float AllocatedScale =
            Clamp(VrConfig.Float("render_scale", 0.9f), 0.1f, 2.0f);

        /// <summary>What the player is asking for now.</summary>
        public static float Wanted { get; private set; } = AllocatedScale;

        /// <summary>
        /// The fraction of the allocated target to shade, so that the ABSOLUTE
        /// resolution comes out at Wanted.
        ///
        /// Wanted / AllocatedScale rather than Wanted, because the target is
        /// already AllocatedScale of the runtime's recommendation - shading
        /// Wanted OF THAT would compound the two and land at the product.
        ///
        /// Clamped at 1: the target has no more pixels than it was made with.
        /// </summary>
        public static float LiveFactor
        {
            get
            {
                if (!LiveEnabled || AllocatedScale <= 0.01f)
                    return 1.0f;

                return Clamp(Wanted / AllocatedScale, 0.25f, 1.0f);
            }
        }

        /// <summary>True when the request is above what the targets can serve,
        /// so the extra really is waiting for a relaunch.</summary>
        public static bool CappedByLaunchSize => LiveEnabled && Wanted > AllocatedScale + 1e-3f;

        public static void SetWanted(float scale)
        {
            Wanted = Clamp(scale, 0.1f, 2.0f);
        }

        // =====================================================================
        // APPLY: the half the slider could not do on its own
        // =====================================================================
        //
        // Everything above makes the scene shade at a different fraction of the
        // eye target. That is the whole of the DOWNWARD direction and none of the
        // upward one, because a target allocated at 0.6 has no 0.9 in it. Apply
        // reallocates the pair, which is the only thing that can.
        //
        // WHY THIS IS A STATE MACHINE AND NOT A METHOD
        //
        // The work is split across two threads that each own half of it. The eye
        // TEXTURES belong to the engine - managed asks rgl for a render target and
        // the capture hook takes whatever rgl creates, some frames later, on rgl's
        // own thread. The SWAPCHAINS belong to OpenXR and may only be touched from
        // the frame loop. Neither side can do the other's half, and neither may
        // block waiting for it, so the sequence runs a step per tick.
        //
        // THE ORDER IS THE SAFETY, and it is worth reading as a whole:
        //
        //   1  let go     AFR stops treating the engine's view as retargeted.
        //   2  request    native drains the frames already in flight, then drops
        //                 its own reference to the eye textures, tears down
        //                 everything derived from them, and works out the new
        //                 size. IT DOES NOT TOUCH THE SWAPCHAINS.
        //   3  release    our Texture objects go - and rgl's pool is flushed with
        //                 them, or the next allocation comes back recycled and
        //                 invisible to the capture hook.
        //   4  allocate   a new pair at the size native chose, and wait for rgl
        //                 to service it, which VrStereoRenderer already does.
        //   5a swap       got them: NOW the swapchains are rebuilt to match. This
        //                 is the first irreversible thing that has happened.
        //   5b roll back  did not get them: ask native for the OLD size again and
        //                 allocate that instead. The swapchains still fit it, so
        //                 the picture comes back.
        //   6  rebind     the engine's view points at the new target.
        //
        // STEP 5 USED TO COME SECOND, and that is what made a failure a black
        // screen. Destroying working swapchains before the new textures existed
        // left nothing to fall back to when rgl would not produce them, which it
        // does more often than it sounds - see ReleaseAndFlushPool for why.

        public enum ApplyStage
        {
            Idle = 0,
            Requesting = 1,
            AwaitingNative = 2,
            Allocating = 3,
            Swapping = 4,
            Failed = 5,
        }

        public static ApplyStage Stage { get; private set; } = ApplyStage.Idle;

        /// <summary>What the panel's status line shows: 0 idle, 1 rebuilding,
        /// 2 live at the new size, 3 failed.</summary>
        public static int PanelState
        {
            get
            {
                if (Stage == ApplyStage.Failed)
                    return 3;
                if (Stage != ApplyStage.Idle)
                    return 1;
                return _everApplied ? 2 : 0;
            }
        }

        private static bool _everApplied;
        private static int _frames;

        /// <summary>Frames to wait for rgl to hand back a new pair before calling
        /// it off. Generous: the engine allocates on its own thread and a loading
        /// hitch in the middle of one is not a failure.</summary>
        private static readonly int AllocateTimeout =
            (int)Clamp(VrConfig.Float("render_scale_allocate_timeout_frames", 600f), 60f, 5000f);

        /// <summary>
        /// The panel's Apply button. Records the request; the work happens a step
        /// per tick from here on.
        /// </summary>
        public static void RequestApply()
        {
            if (Stage != ApplyStage.Idle && Stage != ApplyStage.Failed)
            {
                VrLog.Info("Resolution: a rebuild is already running; this Apply is "
                           + "ignored rather than queued. Press it again when the "
                           + "picture is back.");
                return;
            }

            if (!LiveEnabled)
            {
                VrLog.Warn("Resolution: render_scale_live = 0, so Apply does nothing. "
                           + "The slider stays a next-launch setting.");
                return;
            }

            _frames = 0;
            Stage = ApplyStage.Requesting;

            VrLog.Info($"Resolution: rebuilding the eye pair at {Wanted:0.00}. The view "
                       + "goes black while it happens - there is no eye image to show "
                       + "between releasing the old pair and capturing the new one.");
        }

        /// <summary>One step. Called every tick; does nothing at all when idle.</summary>
        public static void Tick()
        {
            if (Stage == ApplyStage.Idle)
                return;

            try
            {
                switch (Stage)
                {
                    case ApplyStage.Requesting:
                        StepRequest();
                        break;

                    case ApplyStage.AwaitingNative:
                        StepAwaitNative();
                        break;

                    case ApplyStage.Allocating:
                        StepAllocate();
                        break;

                    case ApplyStage.Swapping:
                        StepSwapping();
                        break;
                }
            }
            catch (Exception ex)
            {
                Fail("the rebuild threw", ex);
            }
        }

        private static void StepRequest()
        {
            // Unbind FIRST. Native's drain only means anything if nothing new can
            // start rendering into the old pair while it runs.
            VrAfrRenderer.UnbindForRescale();

            int rc = NativeMethods.bvr_request_rescale(Wanted);
            if (rc != (int)BvrStatus.Ok)
            {
                Fail("native refused the request (" + rc + "); the view is being put "
                     + "back as it was", null);

                // The unbind above has to be undone or the player is left looking
                // at a game that renders to the monitor and nothing else.
                Rebind();
                return;
            }

            _frames = 0;
            Stage = ApplyStage.AwaitingNative;
        }

        private static void StepAwaitNative()
        {
            int stage = NativeMethods.bvr_rescale_stage();

            if (stage == 3)
            {
                Fail("native could not rebuild the swapchains", null);
                return;
            }

            if (stage != 2)
                return;     // still draining; nothing to do but wait

            // Native has released its reference and rebuilt the swapchains. The
            // size it chose is the one our targets must match exactly.
            if (!VrSystem.RefreshRecommendedSize())
            {
                Fail("the new per-eye size could not be read back", null);
                return;
            }

            // ReleaseForRescale, not Release: it also flushes rgl's pool, without
            // which a size used earlier in the session comes back recycled and
            // the capture hook never sees it. See VrStereoRenderer.
            VrStereoRenderer.ReleaseForRescale();
            _frames = 0;
            Stage = ApplyStage.Allocating;
        }

        private static void StepAllocate()
        {
            // VrStereoRenderer's own tick does the allocating and the polling;
            // this only watches for it to finish.
            if (VrStereoRenderer.TexturesReady)
            {
                // ONLY NOW is it worth destroying swapchains that currently work.
                // Up to this line the rescale is entirely undoable.
                if (NativeMethods.bvr_rescale_swap() != (int)BvrStatus.Ok)
                {
                    Fail("native would not swap the swapchains over", null);
                    return;
                }

                _frames = 0;
                Stage = ApplyStage.Swapping;
                return;
            }

            if (++_frames > AllocateTimeout)
                RollBack();
        }

        private static void StepSwapping()
        {
            int stage = NativeMethods.bvr_rescale_stage();

            if (stage == 4)
            {
                Fail("the swapchains could not be rebuilt at the new size", null);
                return;
            }

            if (stage != 0)
            {
                if (++_frames > AllocateTimeout)
                    Fail("native never finished swapping the swapchains", null);
                return;
            }

            // BEFORE the rebind, because rebinding re-applies LiveFactor and
            // LiveFactor divides by this. Set it after, and the first scale
            // applied to the new view would be computed from the size the
            // targets used to be.
            AllocatedScale = Wanted;

            Rebind();

            _everApplied = true;
            _rolledBack = false;
            _frames = 0;
            Stage = ApplyStage.Idle;

            VrLog.Info($"Resolution: live at {Wanted:0.00} without a restart. The eye "
                       + "targets, the swapchains and everything derived from them "
                       + "were rebuilt in place.");
        }

        private static bool _rolledBack;

        /// <summary>
        /// rgl would not give us textures at the new size, so go back to the one
        /// that worked.
        ///
        /// THIS IS WHY THE SWAPCHAINS ARE LEFT ALONE UNTIL THE SWAP. They are
        /// still sized for the pair we just released, so re-allocating at the old
        /// scale lands on something that matches them and the picture comes back.
        /// The earlier version tore them down first, and a failure here meant a
        /// headset with swapchains for an image that was never going to arrive -
        /// black, with nothing to do but restart.
        ///
        /// Once only. If the old size will not allocate either, something larger
        /// is wrong than a resolution change and looping would only keep the view
        /// black while it happened.
        /// </summary>
        private static void RollBack()
        {
            if (_rolledBack)
            {
                Fail("neither the new size nor the old one would allocate", null);
                return;
            }

            _rolledBack = true;

            VrLog.Warn($"Resolution: rgl never handed back eye textures at {Wanted:0.00}. "
                       + $"Rolling back to {AllocatedScale:0.00}, which the swapchains are "
                       + "still sized for - they are deliberately not touched until the "
                       + "new textures exist, so this is a way home rather than a black "
                       + "screen.");

            Wanted = AllocatedScale;

            if (NativeMethods.bvr_rescale_abort(AllocatedScale) != (int)BvrStatus.Ok)
            {
                Fail("native refused the rollback", null);
                return;
            }

            if (!VrSystem.RefreshRecommendedSize())
            {
                Fail("the old per-eye size could not be read back", null);
                return;
            }

            VrStereoRenderer.ReleaseForRescale();
            _frames = 0;
            Stage = ApplyStage.Allocating;
        }

        private static void Rebind()
        {
            MissionScreen screen = ScreenManager.TopScreen as MissionScreen;
            if (screen == null || !VrAfrRenderer.RebindAfterRescale(screen))
            {
                // Not a failure worth shouting about: outside a mission there is
                // no scene view to point anywhere, and the next scene binds the
                // new targets the way it binds them every time.
                VrLog.Info("Resolution: no mission screen to re-point, so the new "
                           + "targets bind when the next scene starts.");
            }
        }

        private static void Fail(string what, Exception ex)
        {
            Stage = ApplyStage.Failed;
            _frames = 0;

            // Let native out of its wait either way, or it submits empty frames
            // until its own timeout - a black headset for far longer than the
            // failure needed.
            try { NativeMethods.bvr_rescale_finish(); } catch { }

            string message = "Resolution: the live rebuild failed - " + what
                             + ". The picture comes back at whatever size the targets "
                             + "are; restart to be sure of it. render_scale_live = 0 "
                             + "turns Apply off entirely.";

            if (ex != null)
                VrLog.Error(message, ex);
            else
                VrLog.Warn(message);
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
