using System;
using BannerlordVR.Interop;
using BannerlordVR.Patches;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Engine.Options;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// World-anchored interface in its true place, per eye (ui_per_eye, TEST 281).
    ///
    /// Every widget pinned to something in the world - settlement and party
    /// nameplates, map event icons, formation and objective markers, damage
    /// numbers - is placed by MBWindowManager.WorldToScreen through a camera. On
    /// the one shared overlay panel that could only ever be right for one
    /// viewpoint, so each landed somewhere between the two eyes.
    ///
    /// Here the camera handed to WorldToScreen is replaced, once per frame, by
    /// one at an EYE whose frustum is exactly the interface panel: the ray from
    /// that eye through the panel rectangle native hangs ui_overlay_distance ahead
    /// of the head. A widget then lands on the panel where that eye's line to its
    /// object crosses it. Native gives each eye its own panel image
    /// (bvr_set_ui_projection says which eye and which head pose this frame's
    /// layout used), so the two fuse at the object's own depth. The HUD and menus
    /// are screen-fixed and land identically in both eyes, as before.
    ///
    /// The eye alternates frame to frame, independent of the scene's eye, so it
    /// works the same in AFR, DR and native stereo and on the map.
    /// </summary>
    public static class VrUiProjection
    {
        private static readonly bool Enabled = VrConfig.Bool("ui_per_eye", false);

        /// <summary>World widgets are laid out through our camera (TEST 590 needs it:
        /// its world page is that camera's frustum).</summary>
        public static bool Projects => Enabled && !_failed && VrCameraDriver.HaveEyeFrames;

        /// <summary>
        /// TEST 621: 1 in missions, as on the map (TEST 401). 0 (the newest layout)
        /// slid the battle markers with the head. TEST 620's 2 - the layout of the
        /// tick the late latch names for the frame's camera - was right at 82+ fps
        /// and broke at 74-76, where the camera read 2 back: rgl reads the camera
        /// when it renders, the interface was recorded a tick earlier.
        /// </summary>
        private static readonly int MissionEyeLag = Math.Max(0, Math.Min(2, VrConfig.Int("ui_eye_lag", 1)));
        /// <summary>
        /// TEST 401: 1 on the map. Its interface is presented a frame after the
        /// tick that laid it out, like its world (TEST 277), so pairing each image
        /// with the NEWEST publication gave each eye the other eye's layout, hung
        /// from the next frame's head pose: "labels slide with my head, ghosting".
        /// </summary>
        private static readonly int MapEyeLag = VrConfig.Bool("map_ui_eye_lag", true) ? 1 : 0;
        private static int _sentLag = -1;

        /// <summary>
        /// TEST 283: the panel covers the WHOLE headset field of view, so a world
        /// widget shows wherever its object is, not only inside a screen-sized
        /// rectangle. The HUD and menus keep the size the player chose
        /// (ui_overlay_width at ui_overlay_distance): the game's usable AREA - and
        /// only the area - confines them to that central part of the bigger panel.
        ///
        /// TEST 299 removed the UI-scale half of that. Gauntlet already derives its
        /// own scale from the usable area (UIContext.UpdateScale reads
        /// TwoDimensionContext.Height, which is RealScreen x UsableArea), so calling
        /// ScreenManager.OnScaleChange(UIScale * f) on top of it shrank every HUD
        /// element by f TWICE - 26% of its intended pixels in the worst run. That
        /// was most of the "low resolution UI".
        /// </summary>
        private static readonly bool FillFov = VrConfig.Bool("ui_fill_fov", true);

        /// <summary>
        /// TEST 299b: HOW MUCH of the field of view the panel covers, 0.2 to 1.
        ///
        /// This is the dial the whole TEST 283 -> 285 -> 299 chain was trying to
        /// avoid having. The panel carries ONE backbuffer, so the wider it is spread
        /// the fewer pixels land on each degree: at 1.0 the 2560 px window covers
        /// 4.96 tangent units, which is 516 px per unit against the headset's own
        /// ~1003, and the interface reads soft. At 0 (ui_fill_fov off) the panel is
        /// ui_overlay_width - 1.63 units, 1568 px per unit - and is sharper than the
        /// headset can show, but a formation off to the side has no panel to land on.
        ///
        /// There is no setting that gives both; that is what the dense UI target was
        /// for. Until that works this is the honest trade, and it is a trade only the
        /// player can make, so it is a number and not a decision taken here.
        /// </summary>
        private static readonly float FillPanel = VrConfig.Float("ui_fill_panel", 1f);

        /// <summary>The panel's width as native shows it, metres.</summary>
        private static float _panelWidth;

        /// <summary>The panel's distance as native shows it, metres (TEST 398).</summary>
        private static float _distance;

        private static bool _mapPlane;
        private static bool _split;
        private static bool _loggedSplit;

        /// <summary>
        /// TEST 398 A/B. 1: the map's widgets laid out per eye, alternating - right
        /// at every depth, each eye refreshed every other frame. 0: one image from
        /// the head centre to both eyes every frame - full rate, exact at the depth
        /// you look at, doubled away from it.
        /// </summary>
        private static readonly bool MapPerEye = VrConfig.Bool("map_ui_per_eye", false);

        /// <summary>TEST 596: the world panel keeps no head roll (ui_world_upright).</summary>
        private static readonly bool WorldUpright = VrConfig.Bool("ui_world_upright", true);

        /// <summary>The same in missions (TEST 580). Per eye, each eye's image
        /// refreshed at half the game rate, out of phase with the other: the
        /// formation markers read low frame rate and slid in fast turns, as the
        /// map's labels did before TEST 404.</summary>
        private static readonly bool MissionPerEye = VrConfig.Bool("mission_ui_per_eye", false);

        /// <summary>TEST 580: pre-battle the plane hangs at the battlefield you
        /// look at, up to this far, so head motion moves the markers with their
        /// troops instead of across them.</summary>
        private static readonly float DeployMaxM = Math.Max(VrConfig.Float("deployment_ui_max_m", 150f), 1f);
        private static float _deployDepth;

        /// <summary>The HUD's share of the panel, 1 when not filling.</summary>
        private static float _hudFraction = 1f;
        private static bool _filling;
        private static bool _loggedFill;

        /// <summary>
        /// Application tick, after VrMissionPhase: sizes the panel and the HUD for
        /// this frame and pushes both.
        /// </summary>
        public static void Tick()
        {
            try
            {
                // TEST 379: the campaign map draws a frame out of step with a
                // mission (TEST 277), so it gets its own eye lag.
                int lag = VrMapStereo.Live ? MapEyeLag : MissionEyeLag;
                if (lag != _sentLag)
                {
                    _sentLag = lag;
                    NativeMethods.bvr_set_ui_eye_lag(lag);
                    VrLog.Info($"UI per eye: eye lag {lag}{(lag == 2 ? " (by build, TEST 620)" : "")} for the "
                               + (VrMapStereo.Live ? "campaign map" : "missions")
                               + " (TEST 379; map_ui_eye_lag / ui_eye_lag).");
                }

                // TEST 590: the split has its own world panel and leaves the usable
                // area alone, so the TEST 283 fill never runs with it.
                bool split = VrUiSplit.Active;
                bool fill = Enabled && FillFov && !_failed && VrSystem.IsActive && !split
                            && VrMissionPhase.UiWantsOverlay && VrCameraDriver.HaveEyeFrames;

                float d = VrUiSettings.Distance;
                float hudW = VrUiSettings.Width;

                // TEST 398: ON THE MAP THE PANEL HANGS AT THE DEPTH YOU LOOK AT, scaled
                // with it so every widget keeps its angular size. Its errors - the
                // 36 Hz per-eye refresh against head motion, a mispaired eye - all
                // scale with 1/label depth - 1/panel depth; at 2.47 m against a table
                // 0.3-1 m away that is what read as "low framerate" and "doubled when
                // I get close". A pinned menu keeps the panel's own distance.
                bool mapPlane = Enabled && !_failed && VrMapStereo.Live && !VrLaser.Pinned
                                && VrMapStereo.UiDepthMetres > 0f;
                bool deployPlane = Enabled && !_failed && !mapPlane && VrMissionPhase.Deploying
                                   && !VrLaser.Pinned && DeploymentDepth(d) > 0f;
                if (!deployPlane)
                    _deployDepth = 0f;
                if (mapPlane || deployPlane)
                {
                    float k = (mapPlane ? VrMapStereo.UiDepthMetres : _deployDepth) / d;
                    d *= k;
                    hudW *= k;
                }
                _distance = d;

                if (split)
                {
                    // TEST 590: the world panel is ui_world_fov of the view across,
                    // in the world page's shape; the HUD keeps its own width beside it
                    // at the same distance (published below).
                    float tanH = 0f, tanV = 0f;
                    for (int eye = 0; eye < 2; eye++)
                    {
                        VrCameraDriver.EyeTangents(eye, out float l, out float r, out float u, out float dn);
                        tanH = Math.Max(tanH, Math.Max(Math.Abs(l), Math.Abs(r)));
                        tanV = Math.Max(tanV, Math.Max(Math.Abs(u), Math.Abs(dn)));
                    }
                    float aspect = VrUiSplit.WorldAspect;
                    _panelWidth = VrUiSplit.WorldFov * 2f * d * Math.Max(tanH, tanV * aspect);
                    float f = NativeFit(_panelWidth, d);
                    _panelWidth *= f;
                    d *= f;
                    hudW *= f;
                    _distance = d;
                    _hudFraction = 1f;
                    AutoSupersample = 0f;
                    NativeMethods.bvr_set_ui_geometry(_panelWidth, d);

                    if (!_loggedSplit)
                    {
                        _loggedSplit = true;
                        VrLog.Info($"UI split (TEST 590): world panel {_panelWidth:F2} x {_panelWidth / aspect:F2} m "
                                   + $"at {d:F2} m ({VrUiSplit.WorldFov:P0} of the view; FOV tangents H {tanH:F2} "
                                   + $"V {tanV:F2}); world page {VrUiSplit.WorldW:F0}x{VrUiSplit.WorldH:F0} px, HUD "
                                   + $"page {VrUiSplit.HudW:F0}x{VrUiSplit.HudH:F0} px at x {VrUiSplit.HudX:F0}, "
                                   + $"HUD {hudW:F2} m wide.");
                    }
                }
                else if (fill)
                {
                    float tanH = 0f, tanV = 0f;
                    for (int eye = 0; eye < 2; eye++)
                    {
                        VrCameraDriver.EyeTangents(eye, out float l, out float r, out float u, out float dn);
                        tanH = Math.Max(tanH, Math.Max(Math.Abs(l), Math.Abs(r)));
                        tanV = Math.Max(tanV, Math.Max(Math.Abs(u), Math.Abs(dn)));
                    }

                    // The panel keeps the backbuffer's aspect, so it is sized by
                    // whichever axis needs more; 5% so the edge never shows.
                    float aspect = Screen.RealScreenResolutionWidth
                                   / Math.Max(Screen.RealScreenResolutionHeight, 1f);
                    float full = 2f * d * Math.Max(tanH, tanV * aspect) * 1.05f;
                    full *= Math.Max(0.2f, Math.Min(1f, FillPanel));

                    _panelWidth = Math.Max(full, hudW);
                    _hudFraction = Math.Max(0.2f, Math.Min(1f, hudW / _panelWidth));
                    float f = NativeFit(_panelWidth, d);
                    _panelWidth *= f;
                    d *= f;
                    hudW *= f;
                    _distance = d;

                    NativeMethods.bvr_set_ui_geometry(_panelWidth, d);

                    if (HmdResolution && !_resolutionAsked)
                        ApplyHmdResolution(tanH, tanV);

                    // What the UI target has to be to match the headset's own pixel
                    // density across this panel, computed HERE because this is where
                    // the tangents and the panel width already are. See VrUiTarget.
                    float pxPerTanEye = VrSystem.RecommendedEyeWidth / (2f * Math.Max(tanH, 1e-3f));
                    float pxPerTanWindow = Screen.RealScreenResolutionWidth
                                           / Math.Max(_panelWidth / d, 1e-3f);
                    AutoSupersample = pxPerTanWindow > 1f ? pxPerTanEye / pxPerTanWindow : 0f;

                    if (!_loggedFill)
                    {
                        _loggedFill = true;
                        VrLog.Info($"UI fills the view: panel {_panelWidth:F2} m wide at {d:F2} m "
                                   + $"(FOV tangents H {tanH:F2} V {tanV:F2}); HUD kept at "
                                   + $"{hudW:F2} m = {_hudFraction:P0} of it by the usable area "
                                   + $"alone. The headset wants {pxPerTanEye:F0} px per tangent "
                                   + $"unit where the window gives {pxPerTanWindow:F0} "
                                   + $"({AutoSupersample:F2}x).");
                    }
                }
                else
                {
                    float f = NativeFit(hudW, d);
                    hudW *= f;
                    d *= f;
                    _distance = d;

                    // Every frame on the map (the depth moves), and once on leaving it.
                    if (_filling || mapPlane || deployPlane || _mapPlane || _split)
                        NativeMethods.bvr_set_ui_geometry(hudW, d);

                    _panelWidth = hudW;
                    _hudFraction = 1f;
                    AutoSupersample = 0f;
                }

                // The HUD hangs at the panel's distance (native's ui_overlay_distance IS
                // the published geometry), so it takes the same depth and fit scale.
                // TEST 644: 643 sent the unscaled width here - wrong, reverted.
                VrUiSplit.Publish(hudW);
                _filling = fill;
                _split = split;
                _mapPlane = mapPlane || deployPlane;

                // What the window really became - the one number that says whether
                // the engine took a size larger than the monitor.
                if (_resolutionCheckTick >= 0 && ++_resolutionCheckTick > 180)
                {
                    _resolutionCheckTick = -1;
                    VrLog.Info($"UI at headset resolution: the game window is now "
                               + $"{Screen.RealScreenResolutionWidth:F0}x"
                               + $"{Screen.RealScreenResolutionHeight:F0} (asked "
                               + $"{_askedW}x{_askedH}).");
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                _hudFraction = 1f;
                VrLog.Error("UI fill failed; the panel goes back to the chosen size.", ex);
            }
        }

        /// <summary>
        /// Hands the game's own usable area back now, ahead of Tick. For a screen
        /// that is about to reload its movie: the relayout it causes is harmless
        /// before the load and, for some vanilla screens, fatal after it. The area
        /// setter relayouts every screen from vanilla's own EarlyUpdate, with none
        /// of our code on the stack. See CustomBattleActivatePatch.
        ///
        /// The name is kept because the callers read as intent, not mechanism; the
        /// UI SCALE half went away with TEST 299, which stopped touching it at all.
        /// </summary>
        public static void RestoreGameScale()
        {
            if (_hudFraction < 0.999f)
            {
                _hudFraction = 1f;
                AccessTools.PropertySetter(typeof(ScreenManager), nameof(ScreenManager.UsableArea))
                    .Invoke(null, new object[] { UsableAreaPatch.Raw });
            }

        }

        /// <summary>
        /// TEST 285: the interface at HEADSET resolution. Every menu, HUD element and
        /// label is drawn into the game window, so the window is sized to the
        /// headset's pixel density across the panel: eye pixels per unit of tangent,
        /// times the panel's tangent span. Its shape follows the field of view, so
        /// the filled panel covers the view exactly with nothing spent off-screen.
        /// Windowed mode only; native rebuilds its copies when the size changes.
        /// </summary>
        private static readonly bool HmdResolution = VrConfig.Bool("ui_hmd_resolution", false);

        private static bool _resolutionAsked;
        private static int _resolutionCheckTick = -1;
        private static int _askedW, _askedH;

        private static void ApplyHmdResolution(float tanH, float tanV)
        {
            _resolutionAsked = true;

            uint eyeW = VrSystem.RecommendedEyeWidth;
            if (eyeW == 0 || tanH <= 0f || tanV <= 0f)
                return;

            int mode = (int)NativeOptions.GetConfig(NativeOptions.NativeOptionsType.DisplayMode);
            if (mode != 1)
            {
                VrLog.Warn($"UI at headset resolution needs the game in Windowed mode "
                           + $"(display mode is {mode}); the interface stays at the window's "
                           + "own size.");
                return;
            }

            // The eye covers 2*tanH of tangent across eyeW pixels (symmetric frustum).
            float pxPerTan = eyeW / (2f * tanH);
            int w = Math.Min(8192, (int)(pxPerTan * 2f * tanH * 1.05f) / 8 * 8);
            int h = Math.Min(8192, (int)(pxPerTan * 2f * tanV * 1.05f) / 8 * 8);

            int curW = (int)Screen.RealScreenResolutionWidth;
            int curH = (int)Screen.RealScreenResolutionHeight;
            if (Math.Abs(curW - w) < 16 && Math.Abs(curH - h) < 16)
            {
                VrLog.Info($"UI at headset resolution: the game window is already {curW}x{curH}.");
                return;
            }

            _askedW = w;
            _askedH = h;
            NativeOptions.SetCustomResolution(w, h);
            // The options screen's own path: only the "size changed" flag set.
            NativeOptions.Apply(0, 0, 0, 0, 0, 0, 1, 0, 0, 0);
            _resolutionCheckTick = 0;

            VrLog.Info($"UI at headset resolution: asked the game window for {w}x{h} "
                       + $"(was {curW}x{curH}; {pxPerTan:F0} px per tangent unit from the "
                       + $"{eyeW} px eye).");
        }

        /// <summary>The share of the screen the HUD is confined to, or false when
        /// the game's own usable area should stand. Read by the EarlyUpdate patch.</summary>
        public static bool HudArea(out float fraction)
        {
            fraction = _hudFraction;
            return _hudFraction < 0.999f;
        }

        /// <summary>
        /// How many times denser than the game window the interface would have to be
        /// rasterised to match the headset's own pixels across the panel, or 0 when
        /// the panel is not filling the view. TEST 299; read by VrUiTarget.
        /// </summary>
        public static float AutoSupersample { get; private set; }

        private static Camera _cam;
        private static readonly object BuildLock = new object();
        private static int _frame = -1;
        private static int _eye;
        private static bool _ready;
        private static bool _failed;
        private static bool _logged;
        private static int _projections;
        private static int _frames;
        private static int _logTick;

        /// <summary>The camera a WorldToScreen call should project through this
        /// frame, or null to leave the game's own.</summary>
        /// <summary>TEST 649: the panel camera as last built, without building it -
        /// For() also publishes the layout pose, and the eye pairing (TEST 620/621)
        /// depends on WHEN that happens. Null before the first build.</summary>
        internal static Camera Built => _ready ? _cam : null;

        public static Camera For()
        {
            // A pinned menu (TEST 378) is one image for both eyes.
            if (!Enabled || _failed || !VrSystem.IsActive || !VrMissionPhase.UiWantsOverlay ||
                VrLaser.Pinned)
                return null;

            try
            {
                // TEST 379: CALLED FROM SEVERAL THREADS AT ONCE. The campaign map's
                // settlement nameplates re-project on TWParallel workers, 16 to a
                // batch (SettlementNameplatesVM.Update). The old unguarded check let
                // two workers both see a new frame and both Build(): the eye flipped
                // twice and the shared camera was re-aimed while others projected
                // through it, so one image held labels laid out from BOTH eyes -
                // doubled, and worst for the near ones, whose disparity is largest.
                // Built once under the lock; the frame number is published only
                // after the camera is aimed, so no thread can take it half-built.
                int frame = Utilities.EngineFrameNo;
                if (frame != System.Threading.Volatile.Read(ref _frame))
                {
                    lock (BuildLock)
                    {
                        if (frame != _frame)
                        {
                            _ready = Build();
                            if (_ready)
                                _frames++;
                            Report();
                            System.Threading.Volatile.Write(ref _frame, frame);
                        }
                    }
                }

                if (!_ready)
                    return null;

                System.Threading.Interlocked.Increment(ref _projections);
                return _cam;
            }
            catch (Exception ex)
            {
                _failed = true;
                VrLog.Error("UI per eye failed; world widgets go back to the shared panel's "
                            + "single viewpoint.", ex);
                return null;
            }
        }

        /// <summary>
        /// TEST 644: "pre-battle markers slide very bad, stable for under 2 s after
        /// a pause". xr_set_ui_geometry clamps the panel to 20 m wide and 20 m away,
        /// and the pre-battle plane goes out to the battlefield (up to 150 m): the
        /// widgets were projected onto a 339 m panel 150 m away while the headset
        /// showed a 20 m one 20 m away - right only at its centre. A pause resets the
        /// depth to 2.75 m, which fits; the EMA then crossed ~9 m (the split's width
        /// limit) inside two seconds. Width and distance are now shrunk together to
        /// fit, so the angular size is unchanged and both sides use the same panel.
        /// </summary>
        private const float NativeMaxM = 20f;

        private static bool _loggedFit;

        private static float NativeFit(float width, float d)
        {
            float f = Math.Min(1f, Math.Min(NativeMaxM / Math.Max(width, 1e-3f), NativeMaxM / Math.Max(d, 1e-3f)));
            if (f < 1f && !_loggedFit)
            {
                _loggedFit = true;
                VrLog.Info($"UI (TEST 644): the panel ({width:F1} m wide at {d:F1} m) is past native's {NativeMaxM:F0} m "
                           + $"- shrunk x{f:F2}, width and distance together, so it looks the same and both sides agree.");
            }
            return f;
        }

        /// <summary>TEST 580: the head's ray to the battlefield, smoothed like the
        /// map's (TEST 398); the sky keeps the last depth.</summary>
        private static float DeploymentDepth(float near)
        {
            Scene scene = Mission.Current?.Scene;
            float scale = VrCameraDriver.WorldScale;
            if (scene == null || scale <= 0f)
                return 0f;

            float want = _deployDepth > 0f ? _deployDepth : near;
            MatrixFrame head = VrCameraDriver.HeadFrame;
            bool any = scene.RayCastForClosestEntityOrTerrain(head.origin, head.origin + head.rotation.f * (DeployMaxM * scale),
                                                              out float hit, out WeakGameEntity _, 0.01f,
                                                              (BodyFlags)67188481);
            // War Sails: the sea surface, not the seabed under it.
            if (VrLaser.WaterDistance(Mission.Current, head.origin, head.rotation.f, out float sea) && (!any || sea < hit))
            {
                hit = sea;
                any = true;
            }
            if (any)
                want = Math.Max(near, Math.Min(DeployMaxM, hit / scale));

            // ponytail: per-frame EMA as on the map; scale by dt if it shows.
            _deployDepth = _deployDepth <= 0f ? want : _deployDepth + (want - _deployDepth) * 0.15f;
            return _deployDepth;
        }

        /// <summary>
        /// TEST 633: PSVR2 at 120 Hz with the game at ~47 fps - every app frame
        /// shown 2-3 times, a quarter late - and the world labels (map, towns,
        /// battle markers) slid and jittered with every head move. They are laid out
        /// for one tick's head pose and hung at a pose paired by COUNT (ui_eye_lag),
        /// which holds when the game keeps the display rate (Quest 72/72) and not
        /// here. Held still in the room within ui_world_follow_deg of the view, the
        /// layout and the hang share one pose, so a mispairing costs nothing; beyond
        /// it the panel glides after the head (ui_world_follow_s). Engaged only while
        /// app frames stay up ui_world_follow_ratio display refreshes or more (off
        /// again under 0.85 of it), so a game at the display rate is untouched.
        /// </summary>
        private static readonly float FollowDeg = VrConfig.Float("ui_world_follow_deg", 6f);
        private static readonly float FollowS = Math.Max(0.02f, VrConfig.Float("ui_world_follow_s", 0.2f));
        // TEST 634: 0 = always. The PSVR2 slid at a true 90/90 too (count pairing is
        // off on that path even at the display rate), and 633's gate sat on its own
        // off threshold (1.19) at 70/90. 1.4 = 633's "only when frames repeat".
        private static readonly float FollowRatio = VrConfig.Float("ui_world_follow_ratio", 0f);
        private static bool _follow;
        private static bool _followHave;
        private static BvrQuat _followQ;
        private static long _followAt;

        private static BvrQuat Follow(BvrQuat h)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float gap = _followAt == 0 ? 1f : (float)((now - _followAt) / (double)System.Diagnostics.Stopwatch.Frequency);
            _followAt = now;

            float ratio = NativeMethods.bvr_get_periods_per_frame();
            bool want = FollowDeg > 0f && (FollowRatio <= 0f || (_follow ? ratio >= FollowRatio * 0.85f : ratio >= FollowRatio));
            if (want != _follow)
            {
                _follow = want;
                VrLog.Info(want
                    ? $"UI per eye (TEST 633): app frames stay up {ratio:F2} display refreshes - the world labels hold still "
                      + $"within {FollowDeg:F0} deg of the view and glide after the head beyond ({FollowS:F2} s)."
                    : $"UI per eye (TEST 633): app frames at the display rate again ({ratio:F2}) - the world labels follow the head.");
            }

            if (!_followHave || gap > 0.25f)
                _followQ = h;
            _followHave = true;

            // Off: glide back onto the head (no jump), then follow it exactly.
            float dead = _follow ? FollowDeg : 0f;
            float tau = _follow ? FollowS : 0.08f;
            float dot = _followQ.X * h.X + _followQ.Y * h.Y + _followQ.Z * h.Z + _followQ.W * h.W;
            float sign = dot < 0f ? -1f : 1f;
            dot = Math.Min(1f, Math.Abs(dot));
            float angle = 2f * (float)Math.Acos(dot) * 57.29578f;
            if (!_follow && angle < 0.2f)
            {
                _followQ = h;
                return h;
            }
            if (angle > dead)
            {
                float t = (1f - (float)Math.Exp(-Math.Min(gap, 0.1f) / tau)) * (angle - dead) / angle;
                float x = _followQ.X + (sign * h.X - _followQ.X) * t, y = _followQ.Y + (sign * h.Y - _followQ.Y) * t;
                float z = _followQ.Z + (sign * h.Z - _followQ.Z) * t, w = _followQ.W + (sign * h.W - _followQ.W) * t;
                float n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                if (n > 1e-6f)
                    _followQ = new BvrQuat { X = x / n, Y = y / n, Z = z / n, W = w / n };
            }
            return _followQ;
        }

        private static bool Build()
        {
            // TEST 398: one image from the head centre to both eyes. Published as
            // eye 2 (TEST 401) so native hangs it where it was laid out, paired by
            // the same eye lag - left unpublished it rode the freshest pose.
            bool centre = VrMapStereo.Live ? !MapPerEye : !MissionPerEye;
            if (!centre)
                _eye ^= 1;

            if (!VrCameraDriver.UiEyeGeometry(_eye, out MatrixFrame head, out Vec3 eyePos,
                                              out BvrVec3 headXr, out BvrQuat headQ))
                return false;

            // TEST 596: the split's world panel (town and party labels) without the
            // head's roll - the labels stay upright and face the player while the
            // head tilts. The panel and this camera share the frame, so each label
            // still lands on its town; only the text stops tilting.
            if (_split && WorldUpright)
            {
                headQ = VrMath.RollFree(headQ);
                head = VrMath.ToBannerlord(headQ, headXr);
            }

            // TEST 633: the split's world panel tags along when frames repeat.
            if (_split && centre)
            {
                headQ = Follow(headQ);
                head = VrMath.ToBannerlord(headQ, headXr);
            }

            if (centre)
                eyePos = head.origin;

            // The panel, as native builds it (fill_ui_quad): centred d ahead of
            // the head, head-oriented, w wide, the backbuffer's aspect.
            float d = _distance > 0f ? _distance : VrUiSettings.Distance;
            float w = _panelWidth > 0f ? _panelWidth : VrUiSettings.Width;
            float h = _split
                ? w / VrUiSplit.WorldAspect   // TEST 590: the world page's own shape
                : w * Screen.RealScreenResolutionHeight / Math.Max(Screen.RealScreenResolutionWidth, 1f);

            // The eye in the head's axes: x right, y forward, z up.
            Vec3 e = head.rotation.TransformToLocal(eyePos - head.origin);
            float dz = d - e.y;
            if (dz < 0.05f)
                return false;

            // Off-axis frustum from the eye through the panel's edges, at the
            // near plane. Ratios, so the map's scale does not enter.
            float n = VrCameraDriver.NearPlaneValue;
            float left = n * (-0.5f * w - e.x) / dz;
            float right = n * (0.5f * w - e.x) / dz;
            float bottom = n * (-0.5f * h - e.z) / dz;
            float top = n * (0.5f * h - e.z) / dz;

            MatrixFrame eyeStage = head;
            eyeStage.origin = eyePos;
            MatrixFrame world = VrCameraDriver.StageMetresToWorld(eyeStage);

            if (_cam == null)
                _cam = Camera.CreateCamera();

            // Projection first, pose last - SetViewVolume can rebuild the frame.
            _cam.SetViewVolume(true, left, right, bottom, top, n, VrCameraDriver.FarPlaneValue);
            _cam.LookAt(world.origin, world.origin + world.rotation.f, world.rotation.u);

            NativeMethods.bvr_set_ui_projection(centre ? 2 : _eye, headXr.X, headXr.Y, headXr.Z,
                                                headQ.X, headQ.Y, headQ.Z, headQ.W);

            if (!_logged)
            {
                _logged = true;
                VrLog.Info($"UI per eye: world widgets projected per eye onto the "
                           + $"{w:F2} x {h:F2} m panel {d:F2} m ahead; eye {_eye} frustum "
                           + $"L{left / n:F3} R{right / n:F3} B{bottom / n:F3} T{top / n:F3}.");
            }

            return true;
        }

        private static void Report()
        {
            if (++_logTick < 450)
                return;

            VrLog.Info($"UI per eye: {_projections} widget projection(s) over {_frames} "
                       + "frame(s) in ~5 s.");
            _logTick = 0;
            _projections = 0;
            _frames = 0;
        }
    }
}
