extern alias NumericsVectors;
using System;
using System.Collections.Generic;
using System.Text;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using Vector2 = NumericsVectors::System.Numerics.Vector2;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 377: the battle HUD on the wrists.
    ///
    /// LEFT inner wrist: vanilla's agent status block - health, horse and shield
    /// bars, weapons and ammo, damage taken (and TAOM's career bar, which lives in
    /// the same container). RIGHT inner wrist: the army strength bar, the kill
    /// feed, and the personal kill notices.
    ///
    /// Nothing is redrawn. Vanilla keeps drawing its HUD into the interface as
    /// always; this only reports WHERE those widgets are, in window pixels, and
    /// native crops exactly those rectangles onto a quad in each hand's grip
    /// space and punches them off the head panel. Same UI, drawn somewhere else.
    ///
    /// The selectors, checked against the v1.3.4 prefabs and TAOM's override:
    ///   MainAgentHUD      the widget whose VisualDefinition is "AgentStatus"
    ///   Scoreboard        the grandparent of PowerListPanel (the comparer and banners)
    ///   MissionSPKillFeed SingleplayerGeneralKillFeedWidget, PersonalKillFeedWidget
    /// The interaction prompts (AgentFocus) and the directional hit indicator
    /// (AgentTakenDamage) are siblings of the AgentStatus container, and the Tab
    /// scoreboard is a sibling of the power bar, so all three stay on the head.
    ///
    /// TEST 523: on the campaign map the map bar is the LEFT hand's palette - the
    /// time controls, the status bar (gold, influence, troops, food) and the nav
    /// buttons, stacked in that order - floating above the controller and turned
    /// to the head (placement mode 2, native). The right hand's laser clicks it
    /// (TEST 524). wrist_hud_float = 1 floats the battle wrists the same way.
    /// </summary>
    internal static class VrWristHud
    {
        private static readonly bool Enabled = VrConfig.Bool("wrist_hud", true);

        /// <summary>Logs the three layers' widget trees once, ~4 s into a
        /// mission, to confirm the selectors against the live game.</summary>
        private static readonly bool Dump = VrConfig.Bool("wrist_hud_dump", false);

        private const int Max = BvrWristRegions.MaxRegions;

        private static readonly string[] LayerNames = { "MainAgentHUD", "Scoreboard", "MissionSPKillFeed", "MapBar", "Tooltip", "ChatLog", "MissionOrderOfBattle", "NavalOrderOfBattle" };

        /// <summary>Per selector: the layer it was found in and the widget, so the
        /// tree is walked once per layer instance rather than every frame. The
        /// kill feed's layer is rebuilt whenever HideBattleUI toggles.</summary>
        private sealed class Slot
        {
            public readonly int Hand;
            public readonly string Layer;
            public readonly Func<Widget, Widget> Find;

            /// <summary>Set: only these children of the found widget make the
            /// region (TEST 523 - the map bar's sides share one parent).</summary>
            public readonly Func<Widget, bool> Children;

            /// <summary>Set: the slot only counts while this holds (TEST 532).</summary>
            public readonly Func<bool> When;

            /// <summary>Looked for again every frame - for a small layer that loads
            /// a new movie each time, like the tooltips (TEST 532).</summary>
            public readonly bool Refind;

            /// <summary>The widget's own rectangle, not its children's - for a box
            /// whose content scrolls past its edges inside a clip (TEST 534).</summary>
            public readonly bool Own;
            public GauntletLayer FoundIn;
            public Widget Widget;

            public Slot(int hand, string layer, Func<Widget, Widget> find, Func<Widget, bool> children = null,
                        Func<bool> when = null, bool refind = false, bool own = false)
            {
                Hand = hand;
                Layer = layer;
                Find = find;
                Children = children;
                When = when;
                Refind = refind;
                Own = own;
            }
        }

        // In the order they stack on the wrist, top first.
        private static readonly Slot[] Slots =
        {
            new Slot(0, "MainAgentHUD", root => First(root, w => w.VisualDefinition?.Name == "AgentStatus"),
                     when: () => !VrMissionPhase.Deploying),
            // TEST 377b: the comparer is a GENERATED prefab class
            // ("..._PowerLevelComparer__DependendPrefab"), so it is found by its
            // own child: PowerListPanel -> the comparer -> the container that also
            // holds both banners.
            // TEST 610: not while B has the full scoreboard open - it is on the HUD
            // page then, the bar inside it.
            new Slot(1, "Scoreboard", root => First(root, w => w.Id == "PowerListPanel")?.ParentWidget?.ParentWidget,
                     when: () => !VrMissionPhase.Deploying && !VrUiSplit.ScoreboardOpen),
            new Slot(1, "MissionSPKillFeed", root => First(root, w => Is(w, "SingleplayerGeneralKillFeedWidget")),
                     when: () => !VrMissionPhase.Deploying),
            new Slot(1, "MissionSPKillFeed", root => First(root, w => w.Id == "PersonalKillFeedWidget"),
                     when: () => !VrMissionPhase.Deploying),

            // The deployment screen's controls share one palette above the left
            // hand. The world markers stay on the head overlay.
            // Move the enclosing strip too: vanilla mouse events stop at an unmoved parent's bounds.
            new Slot(0, "MissionOrderOfBattle", root => First(root, w => w.Id == "CaptainPool")?.ParentWidget,
                     when: () => VrMissionPhase.Deploying),
            new Slot(0, "MissionOrderOfBattle", root => First(root, w => w.Id == "LeftSideFormations"),
                     when: () => VrMissionPhase.Deploying),
            new Slot(0, "MissionOrderOfBattle", root => First(root, w => w.Id == "RightSideFormations"),
                     when: () => VrMissionPhase.Deploying),
            new Slot(0, "MissionOrderOfBattle", root => First(root, w => w.Id == "ActionButtons"),
                     when: () => VrMissionPhase.Deploying),
            // War Sails (NavalDLC/GUI/Prefabs/OrderOfBattle/NavalOrderOfBattle.xml):
            // the same ids; the top panel holds the ship pool above the captains.
            new Slot(0, "NavalOrderOfBattle", root => First(root, w => w.Id == "CaptainPool")?.ParentWidget,
                     when: () => VrMissionPhase.Deploying),
            new Slot(0, "NavalOrderOfBattle", root => First(root, w => w.Id == "LeftSideFormations"),
                     when: () => VrMissionPhase.Deploying),
            new Slot(0, "NavalOrderOfBattle", root => First(root, w => w.Id == "RightSideFormations"),
                     when: () => VrMissionPhase.Deploying),
            new Slot(0, "NavalOrderOfBattle", root => First(root, w => w.Id == "ActionButtons"),
                     when: () => VrMissionPhase.Deploying),

            // TEST 523, SandBox/GUI/Prefabs/Map/MapBar.xml: the time controls, then
            // the bottom-right children of the nav bar's parent (InfoBarWidget and
            // MapGatherArmyButton), then its bottom-left ones (the nav buttons, their
            // frame, the unread badges). Bottom, because the tutorial highlight and
            // the scope targeters sit there too at the default top-left.
            new Slot(0, "MapBar", root => First(root, w => w.Id == "CenterPanel")),
            new Slot(0, "MapBar", root => First(root, w => w.Id == "MapBar")?.ParentWidget,
                     c => c.HorizontalAlignment == HorizontalAlignment.Right && c.VerticalAlignment == VerticalAlignment.Bottom),
            new Slot(0, "MapBar", root => First(root, w => w.Id == "MapBar")?.ParentWidget,
                     c => c.HorizontalAlignment == HorizontalAlignment.Left && c.VerticalAlignment == VerticalAlignment.Bottom),

            // TEST 532: the tooltip of what the laser is on in the palette ("Hit Points",
            // the gold breakdown...) goes onto the right hand's panel, which native sits
            // on top of the palette (mode 4) - not left where the bar used to be. Every
            // tooltip prefab (Hint, PropertyBased, Rundown) is a *TooltipWidget under a
            // full-screen root, and the layer loads a new movie per tooltip.
            new Slot(1, "Tooltip", root => First(root, w => IsTooltip(w) && w.IsVisible), null,
                     () => VrMapStereo.Live && VrLaser.OnPalette && VrUiSplit.Active, refind: true),

            // TEST 534: the campaign chat log on the same panel, under the tooltip and
            // straight on top of the bars (Native SPChatLog.xml, layer "ChatLog"). The
            // box is a fixed size; its message list is longer than the box and clipped,
            // so the box's own rectangle is the region.
            // TEST 592: both only while the split can move them off the head image.
            // With a town menu pinned the image is one full window; the chat's old
            // place then held the menu's picture, which went onto the hand and was
            // cut out of the menu.
            new Slot(1, "ChatLog", root => First(root, w => w.Id == "ChatLogWidget"), null,
                     () => VrMapStereo.Live && VrUiSplit.Active, own: true),
        };

        private static bool IsTooltip(Widget w)
        {
            for (Type t = w.GetType(); t != null; t = t.BaseType)
            {
                if (t.Name.EndsWith("TooltipWidget", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static readonly Dictionary<string, GauntletLayer> Layers = new Dictionary<string, GauntletLayer>();

        private static BvrWristRegions _regions = new BvrWristRegions { Rects = new float[2 * Max * 4] };
        private static float _deployTallest;
        private static bool _published;
        private static bool _dumped;
        private static bool _loggedFirst;

        // --- TEST 377d: the panel hangs off the DRAWN wrist --------------------
        //
        // The first placement was a fixed offset in the controller's grip space,
        // and on this controller the grip frame is not the hand's: moving it "10 cm
        // toward the elbow and 5 cm up" carried it further off (the user's photo -
        // out over the horse's neck). The mod already needs a per-controller grip
        // calibration to make the drawn hand line up, for the same reason.
        //
        // So the panel is placed from the arm that is DRAWN: after the arm solve,
        // the forearm bone (origin at the elbow) and the hand bone (at the wrist)
        // give the forearm, mapped back to the headset's stage by the inverse of
        // what the eyes use (StageMetresToWorld). The face is the palm's outward
        // normal from the controller (OpenXR grip +X: out of the left palm, into the
        // right), squared against the forearm. The panel's top points at the hand.

        private static readonly float Lift = VrConfig.Float("wrist_hud_lift_m", 0.035f);
        private static readonly float Along = VrConfig.Float("wrist_hud_along_m", 0.04f);

        private static readonly bool[] PoseFresh = new bool[2];
        private static readonly bool[] PoseSent = new bool[2];
        private static bool _loggedPose;
        private static bool _loggedTip;
        private static int _tipFrames;

        /// <summary>TEST 523: the battle wrists float above the controllers like the
        /// map palette instead of hanging off the drawn forearm.</summary>
        private static readonly bool FloatInBattle = VrConfig.Bool("wrist_hud_float", false);

        private static bool Floating => VrMapStereo.Live || VrMissionPhase.Deploying || (FloatInBattle && Mission.Current != null);

        private static int _floatSent;

        /// <summary>Placement mode 2 (native floats the panels) for both hands
        /// while <see cref="Floating"/>; on the map 3 for the palette (sized by the
        /// campaign slider - TEST 527) and 4 for the right panel, which then holds
        /// the palette's tooltip on top of it (TEST 532). Sent only on a change.</summary>
        private static void PublishFloat()
        {
            int left = !Floating ? 0 : VrMissionPhase.Deploying ? 5 : VrMapStereo.Live ? 3 : 2;
            int right = !Floating ? 0 : VrMissionPhase.Deploying ? 0 : VrMapStereo.Live ? 4 : 2;
            if (left * 10 + right == _floatSent)
                return;

            bool floating = left != 0;
            _floatSent = left * 10 + right;
            NativeMethods.bvr_set_wrist_pose(0, left, 0, 0, 0, 0, 0, 0, 1);
            NativeMethods.bvr_set_wrist_pose(1, right, 0, 0, 0, 0, 0, 0, 1);
            PoseSent[0] = PoseSent[1] = false;
            VrLog.Info("Wrist HUD (TEST 523): the panels " + (floating
                ? "float above the controllers, facing the head."
                : "are back on the wrists."));
        }

        /// <summary>From the mission tick, straight after the arms are drawn.</summary>
        public static void AfterArms()
        {
            if (!Enabled || Floating)
                return;

            try
            {
                Agent agent = Agent.Main;
                MBAgentVisuals visuals = agent?.AgentVisuals;
                Skeleton skeleton = visuals?.GetSkeleton();
                if (agent == null || !agent.IsActive() || skeleton == null ||
                    NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                    return;

                MatrixFrame agentFrame = visuals.GetFrame();
                for (int hand = 0; hand < 2; hand++)
                {
                    BvrHandState state = hand == 0 ? input.Left : input.Right;
                    if (state.IsActive == 0 ||
                        !VrArmBones.TryChain(visuals, skeleton, hand == 0, out VrArmChain chain))
                        continue;

                    Vec3 elbow = ToStage(agentFrame.TransformToParent(
                        skeleton.GetBoneEntitialFrameWithIndex(chain.Fore)).origin);
                    Vec3 wrist = ToStage(agentFrame.TransformToParent(
                        skeleton.GetBoneEntitialFrameWithIndex(chain.Hand)).origin);

                    // TEST 377h: the first tick after a spawn read 30.8 m - the arm
                    // or the anchor not settled yet. A real forearm is ~0.3 m.
                    Vec3 up = wrist - elbow;
                    float fore = up.Length;
                    if (fore < 0.12f || fore > 0.6f)
                        continue;
                    up.Normalize();

                    Vec3 palm = VrMath.ToBannerlord(state.GripOrientation).s;
                    if (hand == 1)
                        palm = -palm;
                    Vec3 normal = palm - up * Vec3.DotProduct(palm, up);
                    if (normal.Length < 0.2f)
                        continue;
                    normal.Normalize();

                    Vec3 right = Vec3.CrossProduct(up, normal);
                    Vec3 at = wrist + normal * Lift - up * Along;

                    BvrVec3 p = VrMath.ToOpenXr(at);
                    BvrQuat q = QuatFromBasis(VrMath.ToOpenXr(right), VrMath.ToOpenXr(up),
                                              VrMath.ToOpenXr(normal));
                    NativeMethods.bvr_set_wrist_pose(hand, 1, p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W);
                    PoseFresh[hand] = true;
                    PoseSent[hand] = true;

                    if (!_loggedPose)
                    {
                        _loggedPose = true;
                        VrLog.Info($"Wrist HUD (TEST 377d): hung from the drawn {(hand == 0 ? "left" : "right")} "
                                   + $"wrist - forearm {(wrist - elbow).Length:F3} m, panel {Lift:F3} m off it and "
                                   + $"{Along:F3} m toward the elbow.");
                    }
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("Wrist HUD: could not place the panels on the drawn wrists; they stay "
                            + "on the controllers.", ex);
            }
        }

        /// <summary>A world point back to the headset's stage (metres, Bannerlord
        /// axes): the inverse of VrCameraDriver.StageMetresToWorld in a mission.</summary>
        private static Vec3 ToStage(Vec3 world) =>
            VrCameraDriver.Anchor.TransformToLocal(world) / VrCameraDriver.WorldScale;

        /// <summary>The rotation whose columns are x, y, z (orthonormal, right-handed).</summary>
        private static BvrQuat QuatFromBasis(BvrVec3 x, BvrVec3 y, BvrVec3 z)
        {
            float tr = x.X + y.Y + z.Z;
            if (tr > 0f)
            {
                float s = (float)Math.Sqrt(tr + 1f) * 2f;
                return new BvrQuat { X = (y.Z - z.Y) / s, Y = (z.X - x.Z) / s, Z = (x.Y - y.X) / s, W = 0.25f * s };
            }
            if (x.X > y.Y && x.X > z.Z)
            {
                float s = (float)Math.Sqrt(1f + x.X - y.Y - z.Z) * 2f;
                return new BvrQuat { X = 0.25f * s, Y = (y.X + x.Y) / s, Z = (z.X + x.Z) / s, W = (y.Z - z.Y) / s };
            }
            if (y.Y > z.Z)
            {
                float s = (float)Math.Sqrt(1f + y.Y - x.X - z.Z) * 2f;
                return new BvrQuat { X = (y.X + x.Y) / s, Y = 0.25f * s, Z = (z.Y + y.Z) / s, W = (z.X - x.Z) / s };
            }
            float t = (float)Math.Sqrt(1f + z.Z - x.X - y.Y) * 2f;
            return new BvrQuat { X = (z.X + x.Z) / t, Y = (z.Y + y.Z) / t, Z = 0.25f * t, W = (x.Y - y.X) / t };
        }

        private static readonly int[] StaleTicks = new int[2];

        /// <summary>A wrist whose arm has not been drawn for half a second, or with
        /// no mission, goes back to the controller-relative placement. Not on the
        /// first missed tick: a paused mission stops ticking with the arms held
        /// where they were, and the panel should stay on them.</summary>
        private static void ExpirePoses()
        {
            bool mission = Mission.Current != null;
            for (int hand = 0; hand < 2; hand++)
            {
                StaleTicks[hand] = PoseFresh[hand] ? 0 : StaleTicks[hand] + 1;
                PoseFresh[hand] = false;

                if (PoseSent[hand] && (!mission || StaleTicks[hand] > 30))
                {
                    NativeMethods.bvr_set_wrist_pose(hand, 0, 0, 0, 0, 0, 0, 0, 1);
                    PoseSent[hand] = false;
                }
            }
        }

        public static void Tick()
        {
            if (!Enabled)
                return;

            ExpirePoses();
            PublishFloat();

            _regions.CountLeft = 0;
            _regions.CountRight = 0;

            // Slots whose layer is not up are skipped, so the battle's stay off
            // on the map and the map bar's in a battle.
            BeginPlan();
            _deployTallest = 0f;
            if (Mission.Current != null || VrMapStereo.Live)
                Collect();
            VrUiSplit.FitDeploy(_deployTallest);
            PackQueued();
            PlanPrompt();
            CommitMoves();
            VrUiSplit.Report();
            LogRegionChange();

            bool any = _regions.CountLeft + _regions.CountRight > 0;
            if (!any && !_published)
                return;

            _regions.ScreenWidth = Screen.RealScreenResolutionWidth;
            _regions.ScreenHeight = Screen.RealScreenResolutionHeight;
            NativeMethods.bvr_set_wrist_regions(ref _regions);
            _published = any;

            if (any && !_loggedFirst)
            {
                _loggedFirst = true;
                VrLog.Info("Wrist HUD (TEST 377): " + Describe() + " Native shows each wrist "
                           + "when its inner face is turned to the eyes and looked at.");
            }
        }

        // --- TEST 590: the hands' widgets moved into the split's hand bins -------
        //
        // Shelf-packed tallest first (TEST 591), each into the first bin it fits. A widget
        // that fits nowhere stays where vanilla put it and is punched off the head
        // quad as before (TEST 377), so nothing is ever lost.

        private const float BinPad = 8f;
        private static readonly float[] BinX = new float[2];
        private static readonly float[] BinY = new float[2];
        private static readonly float[] BinRow = new float[2];
        private static readonly Dictionary<Widget, Vector2> Planned =
            new Dictionary<Widget, Vector2>();
        private static readonly List<Widget> Moving = new List<Widget>();
        private static int _unplaced;
        private static bool _loggedPack;

        private static readonly Action<EventManager> LayoutDirty =
            HarmonyLib.AccessTools.MethodDelegate<Action<EventManager>>(
                HarmonyLib.AccessTools.Method(typeof(EventManager), "SetLayoutDirty"));

        /// <summary>TEST 636: under the split MainAgentHUD is a hands' layer, laid out
        /// on the whole window with only its wrist pieces moved anywhere visible -
        /// so the "talk" / "pick up" prompt (AgentFocus) showed nowhere. It goes on
        /// the head-locked HUD page instead, centred, a little below the middle.</summary>
        private static void PlanPrompt()
        {
            Widget f = VrHeadHud.Focus;
            if (!VrUiSplit.Active || Mission.Current == null || f == null || f.ParentWidget == null ||
                !Layers.TryGetValue("MainAgentHUD", out GauntletLayer hud) || hud.UIContext?.EventManager == null)
            {
                long at = System.Diagnostics.Stopwatch.GetTimestamp();
                if (Mission.Current != null && at - _promptLogAt > 3 * System.Diagnostics.Stopwatch.Frequency && _promptLogs < 20)
                {
                    _promptLogAt = at;
                    _promptLogs++;
                    VrLog.Info($"Prompt (TEST 637): not planned - split {VrUiSplit.Active}, prompt widget {(f == null ? "NOT FOUND" : "found")}, "
                               + $"parent {(f?.ParentWidget == null ? "none" : "yes")}, MainAgentHUD {(Layers.ContainsKey("MainAgentHUD") ? "up" : "missing")}.");
                }
                RestorePrompt();
                return;
            }

            // TEST 638: "very big and in the centre". It kept the whole window's
            // scale on a page laid out ~0.43x as big. Gauntlet scales per layer
            // (UIContext.CustomScale = Scale x ScaleModifier) and this layer also
            // carries the wrists, so the layer's modifier is the prompt's size and
            // every widget outside the prompt opts out (DoNotUseCustomScale - they
            // then use the plain Scale, as before). Setting its own sizes did
            // nothing: its states put them back (112 px tall at 1.0 and 0.6).
            ScalePrompt(hud, f, PromptScale * VrUiSplit.HudH / Math.Max(1f, Screen.RealScreenResolutionHeight));

            // TEST 637: the prefab's root is a full-window (2560x1440) box - moving
            // it put the text off the page. The visible pieces inside it (through
            // any further full-window boxes) move instead, together, keeping their
            // offsets: the same fixed point as the hand bins.
            PromptPieces.Clear();
            Pieces(f, PromptPieces, Screen.RealScreenResolutionWidth, Screen.RealScreenResolutionHeight);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Widget w in PromptPieces)
            {
                x0 = Math.Min(x0, w.GlobalPosition.X);
                y0 = Math.Min(y0, w.GlobalPosition.Y);
                x1 = Math.Max(x1, w.GlobalPosition.X + w.Size.X);
                y1 = Math.Max(y1, w.GlobalPosition.Y + w.Size.Y);
            }
            if (PromptPieces.Count == 0)
                return;

            EventManager em = hud.UIContext.EventManager;
            float tx = VrUiSplit.HudX + 0.5f * VrUiSplit.HudW - 0.5f * (x1 - x0) - em.LeftUsableAreaStart;
            float ty = PromptDown * VrUiSplit.HudH - 0.5f * (y1 - y0) - em.TopUsableAreaStart;
            foreach (Widget w in PromptPieces)
                Planned[w] = new Vector2(tx + w.GlobalPosition.X - x0, ty + w.GlobalPosition.Y - y0);

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _promptLogAt < 3 * System.Diagnostics.Stopwatch.Frequency || _promptLogs >= 20)
                return;
            _promptLogAt = now;
            _promptLogs++;
            VrLog.Info($"Prompt (TEST 637): {PromptPieces.Count} piece(s) of the prompt, {x1 - x0:0}x{y1 - y0:0} at {x0:0},{y0:0}, "
                       + $"to {tx:0},{ty:0} on the HUD page {VrUiSplit.HudX:0},0 {VrUiSplit.HudW:0}x{VrUiSplit.HudH:0}.");
        }

        private static readonly List<Widget> PromptPieces = new List<Widget>();

        /// <summary>TEST 638: the prompt's size against the HUD page's own scale, and
        /// how far down the HUD page its middle sits (0 top, 1 bottom).</summary>
        private static readonly float PromptScale = VrConfig.Float("hud_prompt_scale", 0.6f);
        private static readonly float PromptDown = VrConfig.Float("hud_prompt_down", 0.8f);

        private static GauntletLayer _promptLayer;

        private static void ScalePrompt(GauntletLayer hud, Widget prompt, float k)
        {
            _promptLayer = hud;
            if (Math.Abs(hud.UIContext.ScaleModifier - k) > 1e-4f)
                hud.UIContext.ScaleModifier = k;   // the game puts 1 back on a relayout
            OptOut(hud.UIContext.Root, prompt);
        }

        /// <summary>Everything but the prompt (and the boxes holding it) keeps the
        /// layer's plain scale. Every tick: the game adds widgets as it goes.</summary>
        private static bool OptOut(Widget w, Widget prompt)
        {
            if (ReferenceEquals(w, prompt))
            {
                w.DoNotUseCustomScale = false;
                return true;
            }
            bool holds = false;
            for (int i = 0; i < w.ChildCount; i++)
            {
                Widget c = w.GetChild(i);
                if (c != null && OptOut(c, prompt))
                    holds = true;
            }
            w.DoNotUseCustomScale = true;
            return holds;
        }

        /// <summary>The split is off (a pause, a menu): one scale for the whole layer.</summary>
        private static void RestorePrompt()
        {
            if (_promptLayer == null)
                return;
            if (!_promptLayer.IsFinalized && _promptLayer.UIContext != null)
                _promptLayer.UIContext.ScaleModifier = 1f;
            _promptLayer = null;
        }

        /// <summary>The visible children of w, looking through full-window boxes.</summary>
        private static void Pieces(Widget w, List<Widget> into, float sw, float sh)
        {
            for (int i = 0; i < w.ChildCount; i++)
            {
                Widget c = w.GetChild(i);
                if (c == null || !c.IsVisible || c.Size.X < 1f || c.Size.Y < 1f)
                    continue;
                if (c.Size.X >= 0.9f * sw && c.Size.Y >= 0.9f * sh)
                    Pieces(c, into, sw, sh);
                else
                    into.Add(c);
            }
        }

        private static long _promptLogAt;
        private static int _promptLogs;

        private static void BeginPlan()
        {
            for (int b = 0; b < 2; b++)
                BinX[b] = BinY[b] = BinRow[b] = 0f;
            Planned.Clear();
            Queue.Clear();
            _unplaced = 0;
        }

        /// <summary>A free spot of w x h in the bins, window pixels.</summary>
        private static bool Place(float w, float h, out float x, out float y)
        {
            for (int b = 0; b < 2; b++)
            {
                float[] bin = VrUiSplit.HandBins[b];
                float cx = BinX[b], cy = BinY[b], row = BinRow[b];
                if (cx > 0f && cx + w > bin[2])
                {
                    cy += row + BinPad;
                    cx = 0f;
                    row = 0f;
                }
                if (cx + w > bin[2] || cy + h > bin[3])
                    continue;

                x = bin[0] + cx;
                y = bin[1] + cy;
                BinX[b] = cx + w + BinPad;
                BinY[b] = cy;
                BinRow[b] = Math.Max(row, h);
                return true;
            }
            x = y = 0f;
            return false;
        }

        private struct Pending
        {
            public Slot Slot;
            public EventManager Em;
            public float X0, Y0, W, H;
            public int Order;
        }

        private static readonly List<Pending> Queue = new List<Pending>();

        /// <summary>The slot's region (page pixels, as measured now), queued for
        /// <see cref="PackQueued"/>.</summary>
        private static void PlanMove(Slot slot, EventManager em, float x0, float y0, float w, float h) =>
            Queue.Add(new Pending { Slot = slot, Em = em, X0 = x0, Y0 = y0, W = w, H = h, Order = Queue.Count });

        /// <summary>TEST 591: tallest first. In slot order the 660x403 campaign chat
        /// came last onto a nearly full bin and stayed punched in place - a hole in
        /// the world panel where the old bar was. A shelf's height is set by its first
        /// piece, so the tall ones go first. Ties keep slot order, so the plan does
        /// not reshuffle from frame to frame. Each widget that carries a region keeps
        /// its offset from the region's corner.</summary>
        private static void PackQueued()
        {
            // TEST 630: the tooltip always last. The money's tall one sorted first,
            // moved every bar, and the relayout dropped the hover - the tooltip
            // closed, the bars moved back (white/red flicker, no tooltip).
            Queue.Sort((a, b) =>
            {
                bool ta = a.Slot.Layer == "Tooltip", tb = b.Slot.Layer == "Tooltip";
                if (ta != tb)
                    return ta ? 1 : -1;
                return a.H != b.H ? b.H.CompareTo(a.H) : a.Order.CompareTo(b.Order);
            });
            foreach (Pending p in Queue)
            {
                if (!Place(p.W, p.H, out float tx, out float ty))
                {
                    _unplaced++;
                    continue;
                }

                // Window to page pixels.
                tx -= p.Em.LeftUsableAreaStart;
                ty -= p.Em.TopUsableAreaStart;

                Moving.Clear();
                if (p.Slot.Children == null)
                    Moving.Add(p.Slot.Widget);
                else
                    for (int i = 0; i < p.Slot.Widget.ChildCount; i++)
                    {
                        Widget child = p.Slot.Widget.GetChild(i);
                        if (p.Slot.Children(child))
                            Moving.Add(child);
                    }

                foreach (Widget m in Moving)
                {
                    Vector2 g = m.GlobalPosition;
                    Planned[m] = new Vector2(tx + g.X - p.X0, ty + g.Y - p.Y0);
                }
            }
            Queue.Clear();
        }

        /// <summary>Publishes the plan when it moved, and re-lays out every layer
        /// that gained, lost or moved a widget.</summary>
        private static void CommitMoves()
        {
            Dictionary<Widget, Vector2> live = VrUiSplit.Shifts;
            bool same = live.Count == Planned.Count;
            if (same)
                foreach (KeyValuePair<Widget, Vector2> kv in Planned)
                    if (!live.TryGetValue(kv.Key, out Vector2 v) ||
                        Vector2.DistanceSquared(v, kv.Value) > 0.25f)
                    {
                        same = false;
                        break;
                    }
            if (same)
                return;

            // A widget inside another moved one rides with it.
            var next = new Dictionary<Widget, Vector2>(Planned.Count);
            foreach (KeyValuePair<Widget, Vector2> kv in Planned)
            {
                bool nested = false;
                for (Widget p = kv.Key.ParentWidget; p != null && !nested; p = p.ParentWidget)
                    nested = Planned.ContainsKey(p);
                if (!nested)
                    next[kv.Key] = kv.Value;
            }

            VrUiSplit.Shifts = next;
            foreach (Widget w in live.Keys)
                Relayout(w);
            foreach (Widget w in next.Keys)
                Relayout(w);

            if (!_loggedPack && next.Count > 0)
            {
                _loggedPack = true;
                VrLog.Info($"Wrist HUD (TEST 590): {next.Count} widget(s) moved into the hand bins "
                           + $"[{Bin(0)}] [{Bin(1)}]; {_unplaced} did not fit and stay punched in place.");
            }
        }

        private static string Bin(int b)
        {
            float[] r = VrUiSplit.HandBins[b];
            return $"{r[0]:0},{r[1]:0} {r[2]:0}x{r[3]:0}";
        }

        private static void Relayout(Widget w)
        {
            try
            {
                EventManager em = w.EventManager;
                if (em != null)
                    LayoutDirty(em);
            }
            catch (Exception)
            {
                // A widget whose movie was released has no context; nothing to lay out.
            }
        }

        private static void Collect()
        {
            Layers.Clear();
            List<ScreenLayer> sorted = ScreenManager.SortedLayers;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i] is GauntletLayer g && !g.IsFinalized &&
                    Array.IndexOf(LayerNames, g.Name) >= 0)
                    Layers[g.Name] = g;
            }

            foreach (Slot slot in Slots)
            {
                if (slot.When != null && !slot.When())
                    continue;

                if (!Layers.TryGetValue(slot.Layer, out GauntletLayer layer) || !layer.IsActive ||
                    layer.UIContext?.Root == null || layer.UIContext.ContextAlpha <= 0f)
                    continue;

                // Re-found when the layer is rebuilt or the widget left the tree; a
                // miss waits for a new layer rather than walking it every frame.
                // TEST 532 probe: a released tooltip keeps its own (detached) movie
                // root as parent, so "left the tree" never fired and the slot held a
                // hidden widget for good. Refind slots look again every frame.
                if (slot.FoundIn != layer || (slot.Widget != null && slot.Widget.ParentWidget == null) ||
                    slot.Refind)
                {
                    slot.FoundIn = layer;
                    slot.Widget = slot.Find(layer.UIContext.Root);
                }

                // TEST 532 never put a tooltip on the panel, and the code reads right:
                // say once what this slot actually sees after a second of pointing.
                if (slot.Refind && !_loggedTip && ++_tipFrames == 90)
                {
                    _loggedTip = true;
                    Widget w = slot.Widget;
                    VrLog.Info("Wrist HUD (TEST 532 probe): pointing at the palette, the tooltip slot "
                               + (w == null
                                   ? "finds no *TooltipWidget under the \"Tooltip\" layer's " + layer.UIContext.Root.ChildCount + " root child(ren)."
                                   : $"has {w.GetType().Name} (base {w.GetType().BaseType?.Name}), visible {w.IsVisible}/"
                                     + $"{w.IsRecursivelyVisible()}, parent {(w.ParentWidget == null ? "none" : w.ParentWidget.GetType().Name)}, "
                                     + $"at {w.GlobalPosition.X:0},{w.GlobalPosition.Y:0} {w.Size.X:0}x{w.Size.Y:0}."));
                }

                if (slot.Widget == null || !slot.Widget.IsRecursivelyVisible())
                    continue;

                // TEST 645: the card columns size the pre-battle layer (VrUiSplit.FitDeploy).
                if (VrLaser.IsDeploymentLayer(slot.Layer) && slot.Widget.Id != null &&
                    slot.Widget.Id.EndsWith("SideFormations", StringComparison.Ordinal))
                    _deployTallest = Math.Max(_deployTallest, slot.Widget.Size.Y);

                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                // TEST 536's clip below is for widgets sharing one place; moved
                // apart by the split they never overlap.
                if (slot.Own && VrUiSplit.Active)
                {
                    x0 = slot.Widget.GlobalPosition.X;
                    y0 = slot.Widget.GlobalPosition.Y;
                    x1 = x0 + slot.Widget.Size.X;
                    y1 = y0 + slot.Widget.Size.Y;
                }
                else if (slot.Own)
                {
                    x0 = slot.Widget.GlobalPosition.X;
                    y0 = slot.Widget.GlobalPosition.Y;
                    x1 = x0 + slot.Widget.Size.X;
                    y1 = y0 + slot.Widget.Size.Y;

                    // TEST 536: the chat box reaches down over the nav bar, and the
                    // panel showed a strip of the bar under the chat. It stops at the
                    // top of any left-hand region it overlaps (those come first).
                    for (int i = 0; i < _regions.CountLeft; i++)
                    {
                        float rx = _regions.Rects[i * 4], ry = _regions.Rects[i * 4 + 1];
                        float rw = _regions.Rects[i * 4 + 2];
                        if (rx < x1 && rx + rw > x0 && ry > y0 && ry < y1)
                            y1 = ry;
                    }
                }
                else if (slot.Children == null)
                {
                    Union(slot.Widget, ref x0, ref y0, ref x1, ref y1);
                }
                else
                {
                    for (int i = 0; i < slot.Widget.ChildCount; i++)
                    {
                        Widget child = slot.Widget.GetChild(i);
                        if (slot.Children(child))
                            Union(child, ref x0, ref y0, ref x1, ref y1);
                    }
                }

                if (x1 - x0 < 1f || y1 - y0 < 1f)
                    continue;

                // TEST 590: page pixels to window pixels (the split moves pages),
                // and the widget into the hand bins, off every head quad.
                EventManager em = layer.UIContext.EventManager;
                if (VrUiSplit.Active)
                    PlanMove(slot, em, x0, y0, x1 - x0, y1 - y0);
                x0 += em.LeftUsableAreaStart;
                x1 += em.LeftUsableAreaStart;
                y0 += em.TopUsableAreaStart;
                y1 += em.TopUsableAreaStart;

                x0 = Math.Max(x0, 0f);
                y0 = Math.Max(y0, 0f);
                x1 = Math.Min(x1, Screen.RealScreenResolutionWidth);
                y1 = Math.Min(y1, Screen.RealScreenResolutionHeight);
                if (x1 - x0 < 1f || y1 - y0 < 1f)
                    continue;

                ref int count = ref (slot.Hand == 0 ? ref _regions.CountLeft : ref _regions.CountRight);
                if (count >= Max)
                    continue;

                int at = (slot.Hand * Max + count) * 4;
                _regions.Rects[at] = x0;
                _regions.Rects[at + 1] = y0;
                _regions.Rects[at + 2] = x1 - x0;
                _regions.Rects[at + 3] = y1 - y0;
                count++;
            }

            if (Dump && !_dumped && (Mission.Current == null || Mission.Current.CurrentTime > 4f))
            {
                _dumped = true;
                DumpTrees();
            }
        }

        /// <summary>The bounds of every visible widget under (and including)
        /// <paramref name="w"/>. A container's own size misses children placed
        /// outside it - the health icons sit at negative X offsets - and the kill
        /// feed grows as entries arrive.</summary>
        private static void Union(Widget w, ref float x0, ref float y0, ref float x1, ref float y1)
        {
            if (!w.IsVisible)
                return;

            if (w.Size.X > 0f && w.Size.Y > 0f)
            {
                x0 = Math.Min(x0, w.GlobalPosition.X);
                y0 = Math.Min(y0, w.GlobalPosition.Y);
                x1 = Math.Max(x1, w.GlobalPosition.X + w.Size.X);
                y1 = Math.Max(y1, w.GlobalPosition.Y + w.Size.Y);
            }

            for (int i = 0; i < w.ChildCount; i++)
                Union(w.GetChild(i), ref x0, ref y0, ref x1, ref y1);
        }

        /// <summary>The widget's class or any base class has this name - prefabs can
        /// be compiled into generated subclasses.</summary>
        private static bool Is(Widget w, string typeName)
        {
            for (Type t = w.GetType(); t != null; t = t.BaseType)
            {
                if (t.Name == typeName)
                    return true;
            }
            return false;
        }

        private static Widget First(Widget w, Func<Widget, bool> match)
        {
            if (w == null)
                return null;
            if (match(w))
                return w;
            for (int i = 0; i < w.ChildCount; i++)
            {
                Widget hit = First(w.GetChild(i), match);
                if (hit != null)
                    return hit;
            }
            return null;
        }

        private static string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("window {0}x{1}; left wrist {2} region(s)", _regions.ScreenWidth,
                            _regions.ScreenHeight, _regions.CountLeft);
            AppendRects(sb, 0, _regions.CountLeft);
            sb.AppendFormat("; right wrist {0} region(s)", _regions.CountRight);
            AppendRects(sb, 1, _regions.CountRight);
            sb.Append('.');
            return sb.ToString();
        }

        // TEST 601 probe: the palette showed the nav bar twice in a new sandbox. Each
        // change of the regions is logged (20 a session), the first three with the map
        // bar's tree: four regions = a slot bug, three = the bar drawn twice.
        private static readonly float[] LastRects = new float[2 * Max * 4];
        private static int _lastLeft = -1, _lastRight = -1, _regionLogs;

        private static void LogRegionChange()
        {
            if (_regionLogs >= 20)
                return;
            bool changed = _regions.CountLeft != _lastLeft || _regions.CountRight != _lastRight;
            for (int i = 0; !changed && i < LastRects.Length; i++)
                changed = Math.Abs(LastRects[i] - _regions.Rects[i]) > 4f;
            if (!changed)
                return;

            _lastLeft = _regions.CountLeft;
            _lastRight = _regions.CountRight;
            Array.Copy(_regions.Rects, LastRects, LastRects.Length);
            _regionLogs++;

            int bars = 0;
            foreach (ScreenLayer l in ScreenManager.SortedLayers)
                if (l is GauntletLayer g && g.Name == "MapBar" && !g.IsFinalized)
                    bars++;
            VrLog.Info($"Wrist HUD (TEST 601): {Describe()} Split {(VrUiSplit.Active ? "on" : "off")}, "
                       + $"{VrUiSplit.Shifts.Count} widget(s) moved, {bars} MapBar layer(s).");

            if (_regionLogs <= 3 && VrMapStereo.Live && Layers.TryGetValue("MapBar", out GauntletLayer bar) &&
                bar.UIContext?.Root != null)
            {
                var sb = new StringBuilder();
                int lines = 0;
                DumpWidget(bar.UIContext.Root, 0, sb, ref lines);
                VrLog.Info("Wrist HUD (TEST 601): the MapBar layer:\n" + sb);
            }
        }

        private static void AppendRects(StringBuilder sb, int hand, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int at = (hand * Max + i) * 4;
                sb.AppendFormat(" [{0:0},{1:0} {2:0}x{3:0}]", _regions.Rects[at], _regions.Rects[at + 1],
                                _regions.Rects[at + 2], _regions.Rects[at + 3]);
            }
        }

        private static void DumpTrees()
        {
            foreach (string name in LayerNames)
            {
                if (!Layers.TryGetValue(name, out GauntletLayer layer) || layer.UIContext?.Root == null)
                {
                    VrLog.Info("Wrist HUD dump: no layer \"" + name + "\" is up.");
                    continue;
                }

                var sb = new StringBuilder();
                int lines = 0;
                DumpWidget(layer.UIContext.Root, 0, sb, ref lines);
                VrLog.Info("Wrist HUD dump, layer \"" + name + "\" (active " + layer.IsActive
                           + ", alpha " + layer.UIContext.ContextAlpha + "):\n" + sb);
            }
            VrLog.Info("Wrist HUD dump: chosen regions - " + Describe());
        }

        private static void DumpWidget(Widget w, int depth, StringBuilder sb, ref int lines)
        {
            if (lines >= 150 || depth > 7)
                return;
            lines++;
            sb.Append(' ', depth * 2)
              .AppendFormat("{0} id={1} vd={2} vis={3} at {4:0},{5:0} {6:0}x{7:0}\n", w.GetType().Name,
                            w.Id, w.VisualDefinition?.Name, w.IsVisible, w.GlobalPosition.X,
                            w.GlobalPosition.Y, w.Size.X, w.Size.Y);
            for (int i = 0; i < w.ChildCount; i++)
                DumpWidget(w.GetChild(i), depth + 1, sb, ref lines);
        }
    }
}
