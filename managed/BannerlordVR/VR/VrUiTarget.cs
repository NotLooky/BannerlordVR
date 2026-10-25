using System;
using System.Collections.Generic;
using System.Reflection;
using BannerlordVR.Interop;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 299: the interface RASTERISED at the headset's pixel density, without
    /// touching the game window.
    ///
    /// TEST 283 spread the interface panel over the whole field of view. A 2560
    /// pixel window across that span is about 516 pixels per unit of tangent where
    /// the headset itself has ~924, so every glyph arrives with roughly half the
    /// pixels it needs and the text reads soft. TEST 285 bought those pixels by
    /// growing the game WINDOW to 3568x2576, which the user rejected.
    ///
    /// THE SEAM THIS USES INSTEAD
    ///
    /// Gauntlet does not lay out in render-target pixels. Every mesh it hands the
    /// engine carries its own ScreenSize/ScreenWidth, taken from
    /// Screen.RealScreenResolution (see TwoDimensionEnginePlatform.DrawImage and
    /// DrawText), and that is what turns a widget's pixel rectangle into clip
    /// space. The render target only decides how many samples that rectangle is
    /// rasterised into. So pointing the same layout at a target k times the window
    /// gives the same interface, k times denser - and the mouse, WorldToScreen, the
    /// per-eye panel camera and the usable area never learn that anything moved.
    ///
    /// WHY TWIN VIEWS RATHER THAN RETARGETING THE GAME'S OWN
    ///
    /// Because a render target cannot be taken off again. View.SetRenderTarget
    /// dereferences its argument unconditionally, so null throws - see the note in
    /// VrAfrRenderer.UnbindForRescale. A layer retargeted once could never draw to
    /// the window again, which would take the escape menu, the main menu and the
    /// flat-screen map with it. So each GauntletLayer gets a SECOND
    /// TwoDimensionView bound to BVR_UI, and the layer's own readonly field is
    /// swapped to it only while the interface is on the headset overlay.
    ///
    /// Render order needs no help: ScreenManager.RefreshGlobalOrder runs from
    /// EarlyUpdate every frame and calls GauntletLayer.RefreshGlobalOrder, which
    /// reads that same field - so whichever view is current is the one ordered.
    ///
    /// ui_supersample: 0 auto (match the headset), 1 off (draw into the window as
    /// before), anything else that exact factor.
    /// </summary>
    public static class VrUiTarget
    {
        private static readonly float Factor = VrConfig.Float("ui_supersample", 0f);

        /// <summary>Same budget and reason as VrStereoRenderer.PollFrameBudget: rgl
        /// services the creation on its own thread, some frames later.</summary>
        private const int PollFrameBudget = 900;

        /// <summary>Below -2000, which is where RefreshGlobalOrder starts handing
        /// out orders to active layers. The clear has to run before all of them.</summary>
        private const int ClearOrder = -3000;

        private enum Stage { Idle, Waiting, Ready, Off, Failed }

        private static Stage _stage = Stage.Idle;
        private static int _pollFrames;

        private static Texture _target;
        private static int _width, _height;
        private static float _appliedFactor;

        /// <summary>The view whose only job is to clear BVR_UI to black each frame,
        /// the way GauntletLayer(shouldClear: true) clears the backbuffer.</summary>
        private static TwoDimensionView _clear;

        private static readonly Dictionary<GauntletLayer, TwoDimensionView> Twins =
            new Dictionary<GauntletLayer, TwoDimensionView>();

        /// <summary>Each layer's OWN view, kept because the field it lives in is the
        /// field we overwrite. Restoring reads from here, never from the field.</summary>
        private static readonly Dictionary<GauntletLayer, TwoDimensionView> Originals =
            new Dictionary<GauntletLayer, TwoDimensionView>();

        /// <summary>The layers whose field currently points at a twin.</summary>
        private static readonly HashSet<GauntletLayer> Swapped = new HashSet<GauntletLayer>();

        private static readonly FieldInfo LayerViewField =
            AccessTools.Field(typeof(GauntletLayer), nameof(GauntletLayer.TwoDimensionView));

        private static readonly FieldInfo PlatformViewField =
            AccessTools.Field(typeof(TwoDimensionEnginePlatform), "_view");

        private static bool _sourceFlag;
        private static bool _loggedSwap;

        /// <summary>
        /// TEST 299a: write BVR_UI to a file once, a few seconds after the swap goes
        /// live. This is the one measurement that says where the interface went when
        /// nothing reaches the headset, and it splits the three candidates apart:
        ///
        ///   the image holds the interface, laid out correctly -> the target is fine
        ///     and the fault is downstream, in the key or the submit;
        ///   the image is black -> rgl does not honour SetRenderTarget on a
        ///     TwoDimensionView, so the meshes went to the screen pass as usual;
        ///   the interface is there but shoved off the right and bottom -> rgl scales
        ///     the 2D meshes by the target size on top of ScreenSize, and the fix is
        ///     View.SetScale on the twin.
        /// </summary>
        private static readonly bool Dump = VrConfig.Bool("ui_target_dump", true);

        private static int _liveFrames;
        private static bool _dumped;

        /// <summary>
        /// Application tick, straight after VrUiProjection.Tick - which is where the
        /// panel size and the factor that matches it are worked out.
        /// </summary>
        public static void Tick()
        {
            if (_stage == Stage.Off || _stage == Stage.Failed)
                return;

            try
            {
                if (_stage == Stage.Idle)
                    Allocate();
                else if (_stage == Stage.Waiting)
                    Poll();

                if (_stage != Stage.Ready)
                    return;

                bool want = VrSystem.IsActive && VrMissionPhase.UiWantsOverlay;

                if (want)
                    SwapIn();
                else
                    SwapOutAll();

                // Only while the swap is actually live. Native keys and submits
                // whichever image this names, and naming the wrong one shows a
                // frame of the other.
                bool live = Swapped.Count > 0;
                if (live != _sourceFlag)
                {
                    _sourceFlag = live;
                    NativeMethods.bvr_set_ui_source(live ? 1 : 0);
                }

                if (live)
                {
                    _clear.BeginFrame();
                    _clear.EndFrame();

                    // Four seconds in, so a battle HUD has had time to be up.
                    if (Dump && !_dumped && ++_liveFrames > 240)
                        DumpOnce();
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("The dense UI target failed; the interface goes back to "
                            + "the game window's own pixels.", ex);
                Abandon();
            }
        }

        private static void Allocate()
        {
            if (Factor > 0f && Factor < 1.01f)
            {
                _stage = Stage.Off;
                VrLog.Info("ui_supersample = " + Factor.ToString("F2")
                           + ": the interface is drawn into the game window, as before.");
                return;
            }

            float k = Factor > 0f ? Factor : VrUiProjection.AutoSupersample;

            // Auto stays 0 until the panel is actually filling the view, which is
            // the only state this is worth anything in. Stay Idle and ask again.
            if (k <= 0f)
                return;

            if (k < 1.01f)
            {
                _stage = Stage.Off;
                VrLog.Info($"UI target: the window already gives {k:F2}x of the headset's "
                           + "own density across the panel; nothing to gain.");
                return;
            }

            float w = Screen.RealScreenResolutionWidth;
            float h = Screen.RealScreenResolutionHeight;
            if (w < 1f || h < 1f)
                return;

            // The panel's aspect comes from this texture (native derives the quad's
            // height from uiHeight/uiWidth), so the height follows the WIDTH rather
            // than being scaled independently - that keeps the shape exact where
            // rounding each axis on its own would not.
            int tw = (int)(w * k + 0.5f);
            if (tw > 8192) tw = 8192;

            int th = (int)(tw * h / w + 0.5f);
            if (th > 8192)
            {
                th = 8192;
                tw = (int)(th * w / h + 0.5f);
            }

            _appliedFactor = tw / w;

            if (NativeMethods.bvr_begin_ui_capture((uint)tw, (uint)th) != (int)BvrStatus.Ok)
            {
                _stage = Stage.Failed;
                VrLog.Warn("bvr_begin_ui_capture refused; no dense UI target.");
                return;
            }

            _target = Texture.CreateRenderTarget(
                "BVR_UI", tw, th,
                autoMipmaps: false, isTableau: false,
                createUninitialized: false, always_valid: true);

            if (_target == null)
            {
                _stage = Stage.Failed;
                VrLog.Warn("Texture creation returned null for the UI target.");
                return;
            }

            _width = tw;
            _height = th;
            _pollFrames = 0;
            _stage = Stage.Waiting;

            VrLog.Info($"UI target: asked for BVR_UI {tw}x{th} ({_appliedFactor:F2}x the "
                       + $"{w:F0}x{h:F0} window)"
                       + (Factor > 0f ? " from ui_supersample." : " to match the headset."));
        }

        private static void Poll()
        {
            if (NativeMethods.bvr_end_ui_capture() == (int)BvrStatus.Ok)
            {
                _clear = TwoDimensionView.CreateTwoDimension("BVR_UI_Clear");
                _clear.SetRenderTarget(_target);
                _clear.SetClearColor(255u);      // opaque black, as shouldClear layers use
                _clear.SetRenderOption(View.ViewRenderOptions.ClearColor, true);
                _clear.SetRenderOrder(ClearOrder);
                _clear.SetEnable(true);

                _stage = Stage.Ready;
                VrLog.Info($"UI target: BVR_UI {_width}x{_height} ({_appliedFactor:F2}x) "
                           + $"captured after {_pollFrames} frame(s).");
                return;
            }

            if (++_pollFrames < PollFrameBudget)
                return;

            VrLog.Warn($"Gave up waiting for the UI target after {_pollFrames} frames; "
                       + "the interface stays at the window's own pixels.");
            Abandon();
        }

        private static void SwapIn()
        {
            int added = 0;

            List<ScreenLayer> layers = ScreenManager.SortedLayers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is GauntletLayer layer) || layer.IsFinalized)
                    continue;

                if (Swapped.Contains(layer) || VrHandTarget.Claims(layer))
                    continue;

                if (!Twins.TryGetValue(layer, out TwoDimensionView twin))
                {
                    twin = TwoDimensionView.CreateTwoDimension("BVR_UI_" + layer.Name);
                    twin.SetRenderTarget(_target);
                    Twins.Add(layer, twin);
                }

                TwoDimensionView original = layer.TwoDimensionView;
                Originals[layer] = original;

                LayerViewField.SetValue(layer, twin);
                PlatformViewField.SetValue(layer.TwoDimensionPlatform, twin);

                // Mirrored, not assumed: a layer can be swapped while inactive, and
                // vanilla's own OnActivate/OnDeactivate will drive the twin from
                // here on because they read the field we just wrote.
                twin.SetEnable(layer.IsActive);
                original.SetEnable(false);

                Swapped.Add(layer);
                added++;
            }

            if (added > 0 && !_loggedSwap)
            {
                _loggedSwap = true;
                VrLog.Info($"UI target: {Swapped.Count} layer(s) on the target. The "
                           + "interface is laid out in window pixels and rasterised at "
                           + $"{_appliedFactor:F2}x that.");
            }
        }

        /// <summary>TEST 531: VrHandTarget leaves a layer this already has.</summary>
        internal static bool IsSwapped(GauntletLayer layer) => Swapped.Contains(layer);

        private static void SwapOutAll()
        {
            if (Swapped.Count == 0)
                return;

            int n = Swapped.Count;
            foreach (GauntletLayer layer in Swapped)
                Restore(layer);

            Swapped.Clear();
            VrLog.Info($"UI target: {n} layer(s) swapped back to the game window.");
        }

        /// <summary>Puts a layer's own view back in both fields. Safe on a layer that
        /// is being finalized, which is the one case that cannot wait for a tick.</summary>
        private static void Restore(GauntletLayer layer)
        {
            if (!Twins.TryGetValue(layer, out TwoDimensionView twin))
                return;

            // The field holds the twin; the original is the one it is not.
            TwoDimensionView current = layer.TwoDimensionView;
            if (current != twin)
                return;     // already restored

            TwoDimensionView original = Originals.TryGetValue(layer, out TwoDimensionView o)
                ? o : null;
            if (original == null)
                return;

            LayerViewField.SetValue(layer, original);
            PlatformViewField.SetValue(layer.TwoDimensionPlatform, original);

            original.SetEnable(layer.IsActive);
            twin.SetEnable(false);
        }

        /// <summary>
        /// A layer is being torn down. Restore it FIRST, so vanilla's OnFinalize
        /// invalidates the layer's own view rather than our twin, then invalidate
        /// the twin ourselves. Without this every screen change leaks one native
        /// view and orphans another.
        /// </summary>
        internal static void Forget(GauntletLayer layer)
        {
            if (!Twins.TryGetValue(layer, out TwoDimensionView twin))
                return;

            Restore(layer);
            Swapped.Remove(layer);
            Twins.Remove(layer);
            Originals.Remove(layer);

            twin.SetEnable(false);
            twin.ManualInvalidate();
        }

        private static void DumpOnce()
        {
            _dumped = true;

            string path = System.IO.Path.Combine(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    @"Mount and Blade II Bannerlord\Logs"),
                "BVR_UI.png");

            try
            {
                _target.SaveToFile(path, false);
                VrLog.Info($"UI target: wrote BVR_UI ({_width}x{_height}) to {path}. "
                           + "That image is what the headset is being handed, before "
                           + "keying - see the note on ui_target_dump.");
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not write the UI target to " + path + ".", ex);
            }
        }

        /// <summary>Gives up for the session and hands every layer back its own view.
        /// The interface is then exactly what it was before TEST 299.</summary>
        private static void Abandon()
        {
            try
            {
                SwapOutAll();
            }
            catch (Exception ex)
            {
                VrLog.Error("Could not hand the UI layers back cleanly.", ex);
            }

            if (_sourceFlag)
            {
                _sourceFlag = false;
                NativeMethods.bvr_set_ui_source(0);
            }

            _stage = Stage.Failed;
        }
    }

    /// <summary>
    /// The one thing a per-frame sweep cannot catch: a layer whose view field is
    /// swapped at the moment it is finalized. See VrUiTarget.Forget.
    /// </summary>
    [HarmonyPatch(typeof(GauntletLayer), "OnFinalize")]
    public static class GauntletLayerFinalizePatch
    {
        private static void Prefix(GauntletLayer __instance)
        {
            VrUiTarget.Forget(__instance);
            VrHandTarget.Forget(__instance);
        }
    }
}
