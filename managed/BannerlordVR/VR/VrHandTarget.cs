using System;
using System.Collections.Generic;
using System.Reflection;
using BannerlordVR.Interop;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 531: the campaign map bar draws into a render target of its own.
    ///
    /// The left-hand palette (TEST 523) was CROPPED out of the one interface
    /// image, and the same rectangles were punched off the head panel - so any
    /// settlement nameplate or tooltip passing under where the bar used to be was
    /// cut out with it ("the 3D UI for towns is cropped in the place of the bar").
    /// One image cannot hold both, so the bar gets its own: the MapBar layer's view
    /// is swapped for a twin bound to BVR_HAND, exactly as VrUiTarget does for the
    /// dense interface, and native crops the palette from that instead. The head
    /// panel then has nothing to cut out.
    ///
    /// A 2D view pointed at a texture had never been shown working here (TEST 299
    /// never ran its probe), so native looks at the target for its first seconds
    /// (bvr_get_hand_source_state). Empty after ~3 s: the view is swapped back and
    /// the palette is cropped from the interface as before. hand_ui_own_target = 0
    /// skips all of this.
    ///
    /// Window size plus one pixel each way, so the capture - "the first render
    /// target of this size" - cannot mistake an engine buffer for it; the layout
    /// is in window pixels either way (see VrUiTarget).
    /// </summary>
    internal static class VrHandTarget
    {
        // TEST 531 RESULT: the target stayed empty - rgl does not draw a 2D view into a
        // texture (this also answers TEST 299). Off unless asked for.
        private static readonly bool Enabled = VrConfig.Bool("hand_ui_own_target", false);

        private const int PollFrameBudget = 900;
        private const int ClearOrder = -3000;

        private enum Stage { Idle, Waiting, Ready, Failed }

        private static Stage _stage = Stage.Idle;
        private static int _pollFrames;
        private static Texture _target;
        private static TwoDimensionView _clear;
        private static TwoDimensionView _twin;
        private static TwoDimensionView _original;
        private static GauntletLayer _layer;
        private static bool _source;
        private static bool _loggedSwap;

        private static readonly FieldInfo LayerViewField =
            AccessTools.Field(typeof(GauntletLayer), nameof(GauntletLayer.TwoDimensionView));

        private static readonly FieldInfo PlatformViewField =
            AccessTools.Field(typeof(TwoDimensionEnginePlatform), "_view");

        /// <summary>The layer is drawing into BVR_HAND; VrUiTarget leaves it alone.</summary>
        public static bool Claims(GauntletLayer layer) => layer != null && ReferenceEquals(layer, _layer);

        /// <summary>Application tick, before VrWristHud.</summary>
        public static void Tick()
        {
            if (!Enabled || _stage == Stage.Failed)
                return;

            try
            {
                bool want = VrSystem.IsActive && VrMapStereo.Live;

                if (want && _stage == Stage.Idle)
                    Allocate();
                else if (_stage == Stage.Waiting)
                    Poll();

                GauntletLayer bar = want && _stage == Stage.Ready ? FindBar() : null;
                if (!ReferenceEquals(bar, _layer))
                    SwapOut();
                if (bar != null && _layer == null)
                    SwapIn(bar);

                SetSource(_layer != null);
                if (_layer == null)
                    return;

                _clear.BeginFrame();
                _clear.EndFrame();

                if (NativeMethods.bvr_get_hand_source_state() == 3)
                    Fail("Hand UI (TEST 531): the map bar's own target stayed empty; it is drawn "
                         + "into the interface again and the palette is cropped from there.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Hand UI (TEST 531): the map bar's own target failed; the palette is "
                            + "cropped from the interface.", ex);
                Fail(null);
            }
        }

        private static void Allocate()
        {
            int w = (int)Screen.RealScreenResolutionWidth + 1;
            int h = (int)Screen.RealScreenResolutionHeight + 1;
            if (w < 2 || h < 2)
                return;

            if (NativeMethods.bvr_begin_hand_capture((uint)w, (uint)h) != (int)BvrStatus.Ok)
            {
                Fail("Hand UI (TEST 531): native refused the capture; the palette is cropped from "
                     + "the interface.");
                return;
            }

            _target = Texture.CreateRenderTarget("BVR_HAND", w, h, autoMipmaps: false, isTableau: false,
                                                 createUninitialized: false, always_valid: true);
            if (_target == null)
            {
                Fail("Hand UI (TEST 531): no render target; the palette is cropped from the interface.");
                return;
            }

            _pollFrames = 0;
            _stage = Stage.Waiting;
            VrLog.Info($"Hand UI (TEST 531): asked for BVR_HAND {w}x{h} for the map bar.");
        }

        private static void Poll()
        {
            if (NativeMethods.bvr_end_hand_capture() == (int)BvrStatus.Ok)
            {
                _clear = TwoDimensionView.CreateTwoDimension("BVR_HAND_Clear");
                _clear.SetRenderTarget(_target);
                _clear.SetClearColor(255u);
                _clear.SetRenderOption(View.ViewRenderOptions.ClearColor, true);
                _clear.SetRenderOrder(ClearOrder);
                _clear.SetEnable(true);

                _twin = TwoDimensionView.CreateTwoDimension("BVR_HAND_MapBar");
                _twin.SetRenderTarget(_target);

                _stage = Stage.Ready;
                VrLog.Info($"Hand UI (TEST 531): BVR_HAND captured after {_pollFrames} frame(s).");
                return;
            }

            if (++_pollFrames >= PollFrameBudget)
                Fail("Hand UI (TEST 531): BVR_HAND never arrived; the palette is cropped from the "
                     + "interface.");
        }

        private static GauntletLayer FindBar()
        {
            List<ScreenLayer> layers = ScreenManager.SortedLayers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i] is GauntletLayer g && g.Name == "MapBar" && !g.IsFinalized)
                    return g;
            }
            return null;
        }

        private static void SwapIn(GauntletLayer bar)
        {
            // The dense interface target got there first; it keeps it.
            if (VrUiTarget.IsSwapped(bar))
                return;

            _original = bar.TwoDimensionView;
            LayerViewField.SetValue(bar, _twin);
            PlatformViewField.SetValue(bar.TwoDimensionPlatform, _twin);
            _twin.SetEnable(bar.IsActive);
            _original.SetEnable(false);
            _layer = bar;

            if (!_loggedSwap)
            {
                _loggedSwap = true;
                VrLog.Info("Hand UI (TEST 531): the map bar draws into BVR_HAND; the head panel "
                           + "keeps everything under it.");
            }
        }

        private static void SwapOut()
        {
            GauntletLayer layer = _layer;
            _layer = null;
            if (layer == null || _original == null)
                return;

            if (ReferenceEquals(layer.TwoDimensionView, _twin))
            {
                LayerViewField.SetValue(layer, _original);
                PlatformViewField.SetValue(layer.TwoDimensionPlatform, _original);
                _original.SetEnable(layer.IsActive);
            }
            _twin?.SetEnable(false);
            _original = null;
        }

        /// <summary>The layer is being finalized: its own view goes back first, so
        /// vanilla invalidates that one and not the twin, which is reused.</summary>
        internal static void Forget(GauntletLayer layer)
        {
            if (Claims(layer))
                SwapOut();
        }

        private static void SetSource(bool live)
        {
            if (live == _source)
                return;
            _source = live;
            NativeMethods.bvr_set_hand_source(live ? 1 : 0);
        }

        private static void Fail(string message)
        {
            SwapOut();
            SetSource(false);
            _stage = Stage.Failed;
            if (message != null)
                VrLog.Warn(message);
        }
    }
}
