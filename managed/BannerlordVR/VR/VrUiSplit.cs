extern alias NumericsVectors;
using System;
using System.Collections.Generic;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Vector2 = NumericsVectors::System.Numerics.Vector2;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 590: ONE INTERFACE IMAGE, THREE PLACES IN IT.
    ///
    /// Every Gauntlet layer draws into the one game window, and rgl will not draw a
    /// TwoDimensionView into a render target of ours (TESTs 299, 531), so the world's
    /// widgets, the HUD and the hands' widgets always shared one picture. Cutting the
    /// hands' widgets out of it (TEST 377 punch rects) also cut every nameplate and
    /// formation marker that happened to pass under them - "cropped" - and one quad
    /// cannot be both head-locked (HUD) and world-anchored (markers) - TEST 299.
    ///
    /// So the image is shared out instead of cut. Every layer still lays out on its
    /// own page (EventManager.PageSize, from its own TwoDimensionEnginePlatform) at its
    /// own origin (Left/TopUsableAreaStart), and those are what this moves:
    ///
    ///   WORLD page, top-left: the world-anchored layers (nameplates, formation and
    ///     siege markers). WorldToScreen is remapped onto it, and vanilla clamps and
    ///     culls its markers against this page, so its edges are the world panel's.
    ///     Native shows it on its own quad, ui_world_fov of the headset's view wide,
    ///     hung where it was laid out.
    ///   HUD page, top-right: every other layer, on a head-locked quad of the size the
    ///     player chose, at the freshest head pose.
    ///   HAND bins, the rest: the hands' widgets are moved there one by one
    ///     (VrWristHud), where no head quad shows them, and cropped onto the hands.
    ///
    /// A gutter separates the pages because vanilla lets a nameplate run 50 px past
    /// its page before culling it. Missions (the pre-battle screen too, TEST 643) and
    /// the campaign map only; a menu or a pinned interface puts everything back on
    /// the full window.
    /// </summary>
    internal static class VrUiSplit
    {
        private static readonly bool Enabled = VrConfig.Bool("ui_split", true);

        /// <summary>The world page's share of the window, across and down.</summary>
        private static readonly float WorldShareW = Clamp(VrConfig.Float("ui_split_world_w", 0.55f), 0.3f, 0.75f);
        private static readonly float WorldShareH = Clamp(VrConfig.Float("ui_split_world_h", 0.80f), 0.5f, 1f);

        /// <summary>How much of the headset's view the world panel covers.</summary>
        internal static readonly float WorldFov = Clamp(VrConfig.Float("ui_world_fov", 0.70f), 0.2f, 1f);

        /// <summary>TEST 620: how big the world page's widgets draw, x the game's UI
        /// scale - battle markers and campaign labels apart. The VR panel's HUD size
        /// only sizes the head-locked quad; the world panel is ui_world_fov of the
        /// view, so the widgets on it are sized here, through the same
        /// UIContext.ScaleModifier the game's own UI scale option sets. Markers stay
        /// on their troops: vanilla places them by ScaledPositionOffset (real pixels)
        /// and grows them about that point.</summary>
        public static float BattleScale { get; private set; } = Clamp(VrConfig.Float("ui_world_scale_battle", 1f), 0.5f, 2f);
        public static float MapScale { get; private set; } = Clamp(VrConfig.Float("ui_world_scale_map", 1f), 0.5f, 2f);

        /// <summary>
        /// TEST 643: the pre-battle screen is split too ("make the 3D UI there
        /// stable like the campaign's and the battle's, and fix the cropping").
        /// Off, its formation markers rode one full-window image at the gaze depth,
        /// and the palette's four regions - two card columns 1413 px tall, the
        /// captains, the buttons - were punched out of it, cutting every marker
        /// that passed under them. Split, its markers (WorldToScreenInsideUsableArea,
        /// remapped) go on the world panel with the battle's pairing, hold and eye
        /// bake, and its cards are moved into the hand bins, off every head quad.
        /// TEST 645: as a hands' layer (full window) - 643 made it a world layer, and
        /// its page clipped the cards moved past it: the palette showed only when a
        /// pause put the image back on the full window. ui_split_deployment = 0 puts
        /// it back on the full window for good.
        /// </summary>
        private static readonly bool InDeployment = VrConfig.Bool("ui_split_deployment", true);

        /// <summary>TEST 643/645: the order-of-battle layer's size, x the game's UI
        /// scale - FITTED (FitDeploy) so its tallest card column fills
        /// ui_deploy_fit of the bin under the HUD (764 px at 1440p; the columns are
        /// 1413 at the game's size). The palette scales them back up to its own size,
        /// so only its formation markers come out smaller.</summary>
        private static float _deployScale = Clamp(VrConfig.Float("ui_world_scale_deploy", 0.45f), 0.15f, 1f);
        private static readonly float DeployFit = Clamp(VrConfig.Float("ui_deploy_fit", 0.85f), 0.3f, 0.98f);
        private static int _deploySettle;

        /// <summary>TEST 645: from VrWristHud, the tallest card column as laid out
        /// now (page px). The layout is linear in the scale, so one step lands; the
        /// re-layout takes a frame, so a change waits 15 ticks before the next.</summary>
        internal static void FitDeploy(float tallest)
        {
            if (!Active || tallest < 1f || HandBins[0][3] < 1f || --_deploySettle > 0)
                return;
            // Measured at the scale in use, which TEST 648 may hold below the fit.
            float next = Clamp(DeployApplied * HandBins[0][3] * DeployFit / tallest, 0.15f, 1f);
            if (Math.Abs(next - _deployScale) <= _deployScale * 0.04f)
                return;
            VrLog.Info($"UI split (TEST 645): pre-battle cards x{_deployScale:F2} -> x{next:F2} - the tallest column "
                       + $"{tallest:F0} px against a {HandBins[0][3]:F0} px bin.");
            _deployScale = next;
            _deploySettle = 15;
        }

        /// <summary>TEST 648: "make the pre-battle group markers smaller". The
        /// markers share the layer with the cards, so the layer takes the smaller of
        /// this and the cards' fit; the palette scales the cards back up (UI at 2x).
        /// 0.45 was the fit on the 19:22 run.</summary>
        public static float DeployMarkerScale { get; private set; } =
            Clamp(VrConfig.Float("ui_world_scale_deploy_markers", 0.30f), 0.15f, 0.60f);

        private static float DeployApplied => Math.Min(_deployScale, DeployMarkerScale);

        /// <summary>From the VR panel; true when it changed.</summary>
        internal static bool SetDeployMarkerScale(float k)
        {
            k = Clamp(k, 0.15f, 0.60f);
            if (Math.Abs(k - DeployMarkerScale) < 1e-3f)
                return false;
            DeployMarkerScale = k;
            _deploySettle = 15;
            return true;
        }

        /// <summary>From the VR panel; true when either changed.</summary>
        internal static bool SetScales(float battle, float map)
        {
            battle = Clamp(battle, 0.5f, 2f);
            map = Clamp(map, 0.5f, 2f);
            if (Math.Abs(battle - BattleScale) < 1e-3f && Math.Abs(map - MapScale) < 1e-3f)
                return false;
            BattleScale = battle;
            MapScale = map;
            return true;
        }

        /// <summary>Layers whose widgets are placed through WorldToScreen. The map's
        /// nameplates, map event icons and trackers all load into MapNameplateLayer.</summary>
        private static readonly HashSet<string> WorldLayers = new HashSet<string>
        {
            "MapNameplateLayer",
            "MissionFormationMarker", "MissionSiegeEngineMarker", "MissionNameMarker",
            "MissionDetectionMarkers", "MissionObjective", "MissionAlarmState", "MissionQuestBar",
            "MissionAgentLockVisualizer", "NavalShipMarker",
        };

        private const int RoleWorld = 1;
        private const int RoleHud = 2;

        public static bool Active { get; private set; }

        /// <summary>Window pixels. The world page sits at 0,0; the HUD page at HudX,0.</summary>
        public static float WorldW { get; private set; }
        public static float WorldH { get; private set; }
        public static float HudX { get; private set; }
        public static float HudW { get; private set; }
        public static float HudH { get; private set; }

        /// <summary>Where the hands' widgets may go, window pixels (x, y, w, h).</summary>
        public static readonly float[][] HandBins = { new float[4], new float[4] };

        // Replaced, never changed after publishing: the getters run on whatever
        // thread lays the layer out.
        private static Dictionary<object, int> _platformRole = new Dictionary<object, int>();
        private static Dictionary<EventManager, int> _managerRole = new Dictionary<EventManager, int>();
        private static readonly List<GauntletLayer> Seen = new List<GauntletLayer>();
        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static BvrUiSplit _sent;
        private static bool _broken;

        /// <summary>Before VrUiProjection: decides the split for this frame and
        /// tells native which parts of the image go where.</summary>
        public static void Tick()
        {
            if (_broken)
                return;

            try
            {
                bool want = Enabled && VrUiSettings.Show && VrUiProjection.Projects
                            && VrMissionPhase.UiWantsOverlay && !VrMissionPhase.UiMenuOpen
                            && (!VrMissionPhase.Deploying || InDeployment) && !VrLaser.Pinned && !VrMenu.Open
                            && (Mission.Current != null || VrMapStereo.Live);

                if (want != Active)
                {
                    Active = want;
                    _deploySettle = 15;   // TEST 645: the layer re-lays out at the new scale first
                    VrLog.Info(want
                        ? $"UI split (TEST 590): world widgets on their own {WorldFov:P0}-of-view panel, the HUD "
                          + "head-locked beside it, the hands' widgets moved off both - nothing is punched."
                        : "UI split (TEST 590): off - the interface is one full-window image again.");
                }

                float sw = Screen.RealScreenResolutionWidth;
                float sh = Screen.RealScreenResolutionHeight;
                if (Active && sw > 0f && sh > 0f)
                {
                    float gap = Math.Max(64f, (float)Math.Round(sw * 0.025f));
                    WorldW = (float)Math.Round(sw * WorldShareW);
                    WorldH = (float)Math.Round(sh * WorldShareH);
                    HudX = WorldW + gap;
                    HudW = sw - HudX;
                    HudH = (float)Math.Round(HudW * sh / sw);
                    Set(HandBins[0], HudX, HudH + gap, HudW, sh - HudH - gap);
                    Set(HandBins[1], 0f, WorldH + gap, HudX, sh - WorldH - gap);
                    Classify();
                    ScaleWorld(VrMapStereo.Live && Mission.Current == null ? MapScale : BattleScale);
                }
                else if (_platformRole.Count > 0)
                {
                    _platformRole = new Dictionary<object, int>();
                    _managerRole = new Dictionary<EventManager, int>();
                    Seen.Clear();
                    ScaleWorld(0f);
                    WorldScaled.Clear();
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                Active = false;
                _platformRole = new Dictionary<object, int>();
                _managerRole = new Dictionary<EventManager, int>();
                Shifts = new Dictionary<Widget, Vector2>();
                var off = new BvrUiSplit();
                NativeMethods.bvr_set_ui_split(ref off);
                VrLog.Error("UI split failed; the interface stays one full-window image.", ex);
            }
        }

        /// <summary>The world panel's shape, width over height.</summary>
        public static float WorldAspect => WorldH > 0f ? WorldW / WorldH : 1f;

        /// <summary>The page a layer's platform lays out on, or false for the game's own.</summary>
        internal static bool PageFor(object platform, out float w, out float h)
        {
            w = h = 0f;
            if (!_platformRole.TryGetValue(platform, out int role))
                return false;
            w = role == RoleWorld ? WorldW : HudW;
            h = role == RoleWorld ? WorldH : HudH;
            return true;
        }

        /// <summary>Where a layer's page starts in the window, or false for vanilla's.</summary>
        internal static bool OriginFor(EventManager manager, out float x, out float y)
        {
            x = y = 0f;
            if (!_managerRole.TryGetValue(manager, out int role))
                return false;
            x = role == RoleWorld ? 0f : HudX;
            return true;
        }

        /// <summary>Hands' layers keep the full window: their widgets go to the hand
        /// bins at the size the wrists were tuned for, and nothing else of theirs shows.</summary>
        private static bool HandLayer(string name) =>
            name == "MainAgentHUD" || (name == "Scoreboard" && !ScoreboardOpen) || name == "MissionSPKillFeed" ||
            VrLaser.IsDeploymentLayer(name) ||   // TEST 645: its markers still land on the world panel
            name == "MapBar" ||
            (name == "ChatLog" && VrMapStereo.Live && Mission.Current == null) ||
            (name == "Tooltip" && VrLaser.OnPalette);

        /// <summary>TEST 610: B opens the FULL scoreboard in the same layer as the
        /// wrist's power bar. As a hands' layer it kept the full window, so the world
        /// quad showed its left 1408 px and the HUD quad the rest from x 1472 - two
        /// copies at two scales ("Side 24 0 0" inside "Attacker Side"). Open, it lays
        /// out on the HUD page like any other panel; closed, the bar goes back to the wrist.</summary>
        internal static bool ScoreboardOpen
        {
            get
            {
                Mission m = Mission.Current;
                if (m == null)
                    return false;
                if (!ReferenceEquals(m, _scoreMission) || _score == null)
                {
                    _scoreMission = m;
                    _score = m.GetMissionBehavior<TaleWorlds.MountAndBlade.GauntletUI.Mission.Singleplayer.MissionGauntletBattleScore>();
                }
                return _score?.DataSource?.ShowScoreboard == true;
            }
        }

        private static Mission _scoreMission;
        private static TaleWorlds.MountAndBlade.GauntletUI.Mission.Singleplayer.MissionGauntletBattleScore _score;

        /// <summary>Roles rebuilt only when the layer list or a role changed.</summary>
        private static void Classify()
        {
            List<ScreenLayer> layers = ScreenManager.SortedLayers;
            bool same = layers.Count == Seen.Count;
            for (int i = 0; same && i < layers.Count; i++)
                same = ReferenceEquals(layers[i], Seen[i]);
            if (same && _platformRole.Count > 0 && !RolesMoved())
                return;

            Seen.Clear();
            var platforms = new Dictionary<object, int>();
            var managers = new Dictionary<EventManager, int>();
            var world = new List<GauntletLayer>();
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is GauntletLayer g))
                {
                    Seen.Add(null);
                    continue;
                }
                Seen.Add(g);
                if (g.IsFinalized || g.UIContext?.EventManager == null || g.TwoDimensionPlatform == null)
                    continue;
                if (HandLayer(g.Name))
                {
                    // TEST 645: the pre-battle layer keeps the full window but is still
                    // sized (FitDeploy) - its cards must fit the bins.
                    if (VrLaser.IsDeploymentLayer(g.Name))
                        world.Add(g);
                    continue;
                }

                int role = WorldLayers.Contains(g.Name) ? RoleWorld : RoleHud;
                platforms[g.TwoDimensionPlatform] = role;
                managers[g.UIContext.EventManager] = role;
                if (role == RoleWorld)
                    world.Add(g);
                if (Logged.Add(g.Name + role))
                    VrLog.Info($"UI split (TEST 590): \"{g.Name}\" on the {(role == RoleWorld ? "world" : "HUD")} page.");
            }
            _platformRole = platforms;
            _managerRole = managers;
            _roleKey = RoleKey();

            // A layer that left the world page goes back to the game's own scale.
            ScaleWorld(0f);
            WorldScaled.Clear();
            WorldScaled.AddRange(world);
        }

        private static readonly List<GauntletLayer> WorldScaled = new List<GauntletLayer>();

        /// <summary>TEST 620: ScaleModifier on each world layer, every tick (k 0 = the game's own) - the
        /// game puts its own back on any relayout (GauntletLayer.UpdateLayout), and
        /// UIContext re-lays out on its next Update when it differs.</summary>
        private static void ScaleWorld(float k)
        {
            for (int i = 0; i < WorldScaled.Count; i++)
            {
                float want = ScreenManager.Scale
                             * (k <= 0f ? 1f : VrLaser.IsDeploymentLayer(WorldScaled[i].Name) ? DeployApplied : k);
                UIContext c = WorldScaled[i].IsFinalized ? null : WorldScaled[i].UIContext;
                if (c != null && Math.Abs(c.ScaleModifier - want) > 1e-4f)
                    c.ScaleModifier = want;
            }
        }

        // The layers whose role follows the context, so a change re-classifies.
        private static int _roleKey;
        private static int RoleKey() => (HandLayer("ChatLog") ? 1 : 0) | (HandLayer("Tooltip") ? 2 : 0)
                                        | (HandLayer("Scoreboard") ? 4 : 0);
        private static bool RolesMoved() => RoleKey() != _roleKey;

        /// <summary>From VrUiProjection, once the plane is sized: the HUD quad is
        /// hudWidthM wide at the world panel's distance. Sent only on a change.</summary>
        public static void Publish(float hudWidthM)
        {
            if (_broken)
                return;

            var s = new BvrUiSplit();
            if (Active)
            {
                s.Active = 1;
                s.ScreenWidth = Screen.RealScreenResolutionWidth;
                s.ScreenHeight = Screen.RealScreenResolutionHeight;
                s.WorldW = WorldW;
                s.WorldH = WorldH;
                s.HudX = HudX;
                s.HudW = HudW;
                s.HudH = HudH;
                s.HudWidthM = hudWidthM;
            }
            if (s.Equals(_sent))
                return;
            _sent = s;
            NativeMethods.bvr_set_ui_split(ref s);
        }

        private static void Set(float[] r, float x, float y, float w, float h)
        {
            r[0] = x;
            r[1] = y;
            r[2] = Math.Max(0f, w);
            r[3] = Math.Max(0f, h);
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        // --- the hands' widgets, moved into the bins (VrWristHud packs them) -----

        /// <summary>Widget -> where its top-left goes, in its page's pixels, applied
        /// after its own layout (UiHandShiftPatch). Replaced, never changed, like the
        /// role maps.</summary>
        internal static Dictionary<Widget, Vector2> Shifts = new Dictionary<Widget, Vector2>();

        // TEST 591: proof the placements really happen - the 590 run planned six
        // and applied none (Widget.Layout inlined), and nothing said so.
        private static int _moved;
        private static int _reportTick;

        internal static void NoteMoved() => System.Threading.Interlocked.Increment(ref _moved);

        /// <summary>Every ~5 s while widgets are planned into the bins.</summary>
        internal static void Report()
        {
            if (++_reportTick < 450)
                return;
            _reportTick = 0;
            int moved = System.Threading.Interlocked.Exchange(ref _moved, 0);
            if (Shifts.Count > 0)
                VrLog.Info($"UI split (TEST 591): {Shifts.Count} hand widget(s) planned into the bins, "
                           + $"{moved} placement(s) applied by layout in ~5 s"
                           + (moved == 0 ? " - NONE, the layout hook is not reached." : "."));
        }
    }
}
