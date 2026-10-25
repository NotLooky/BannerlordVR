extern alias NumericsVectors;

using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.MountAndBlade.ViewModelCollection.OrderOfBattle;
using TaleWorlds.ScreenSystem;
using Vector2 = NumericsVectors::System.Numerics.Vector2;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 378: a laser pointer for every interface outside a fight.
    ///
    /// Native casts the ray, from this frame's controller pose, against the quads
    /// it actually placed - the VR panel, then the flat screen or the interface
    /// quad - draws the beam and the dot, and reports where it landed. This side
    /// decides when the laser is wanted and from which hand, and moves the GAME'S
    /// OWN cursor there, exactly as the stick mouse does (Input.SetMousePosition),
    /// so hover and clicks are vanilla's. Gauntlet hit-tests the cursor and takes
    /// LeftMouseButton as a click even in gamepad mode (UIContext.UpdateInput,
    /// GetClickKeys = LeftMouseButton + ControllerRDown).
    ///
    /// Whichever hand last pulled its trigger points. A clicks outside deployment
    /// (TEST 525); the right grip clicks during deployment. VrGamepad maps that
    /// click to LeftMouseButton while A remains the normal gamepad button there.
    ///
    /// Wanted outside missions always, and inside one only while a menu is up -
    /// never in a fight or free walking. While a menu is up the interface quad is
    /// pinned in the room (bvr_set_ui_pin) so it can be pointed at; the bare
    /// campaign map's HUD stays on the head, because it is a HUD.
    ///
    /// ON THE BARE CAMPAIGN MAP the ray crosses the head panel almost everywhere,
    /// and the settlement and party nameplates are on it (MapNameplateLayer),
    /// drawn where each EYE's line to the settlement crosses it - not where a
    /// hand's ray does. So there the cursor is only taken when the ray is on a
    /// widget of some other layer (the map bar, notifications); otherwise it is
    /// left alone and the terrain stays the A button's, through the map's own
    /// red beam (VrMapStereo).
    ///
    /// TEST 529: the same holds with a menu pinned on the map (a town), so A on
    /// the ground through the menu's empty parts still travels. And on the map
    /// the RIGHT hand always points: A is its button, the terrain ray is its
    /// ray, and the palette is on the left hand - no trigger pull to switch.
    /// </summary>
    internal static class VrLaser
    {
        private static readonly bool Enabled = VrConfig.Bool("laser", true);
        private static readonly bool PinMenus = VrConfig.Bool("ui_pin_menus", true);
        private static readonly float Threshold = VrConfig.Float("gamepad_trigger", 0.5f);
        private static readonly float GripThreshold = VrConfig.Float("gamepad_grip", 0.5f);

        /// <summary>The laser is on the game's interface and owns the cursor.
        /// VrGamepad maps the appropriate button to a left click while true.</summary>
        public static bool Active { get; private set; }

        /// <summary>Deployment's pointing hand owns terrain placement this frame.</summary>
        public static bool GroundActive { get; private set; }

        /// <summary>On the campaign map, the laser is on a widget (the palette, a
        /// pinned menu); the map's own beam to the terrain is hidden meanwhile.</summary>
        public static bool OnMapWidget { get; private set; }

        /// <summary>TEST 532: the laser is on a hand panel (native's target 3) -
        /// on the map, the palette. Its tooltip then goes on the panel above it.</summary>
        public static bool OnPalette { get; private set; }

        /// <summary>The interface quad is pinned in the room for a menu. Per-eye
        /// projection is off meanwhile (VrUiProjection.For).</summary>
        public static bool Pinned { get; private set; }

        /// <summary>0 = left, 1 = right.</summary>
        public static int Hand { get; private set; } = 1;

        private static bool _wasLeft;
        private static bool _wasRight;
        private static Widget _hovered;
        private static bool _loggedPin;
        private static bool _loggedMap;
        private static bool _loggedSeen;

        private static string F(float v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>TEST 649: a window point on the split's world page.</summary>
        private static bool OnWorldPage(Vector2 at) =>
            VrUiSplit.Active && at.X < VrUiSplit.WorldW && at.Y < VrUiSplit.WorldH;

        /// <summary>TEST 649: a world point onto the world page through the panel
        /// camera the markers were laid out with (the one already built).</summary>
        private static bool ProjectToWorldPage(Vec3 point, out Vector2 at)
        {
            at = default(Vector2);
            Camera cam = VrUiProjection.Built;
            if (cam == null)
                return false;

            float x = 0f, y = 0f, w = 0f;
            BannerlordVR.Patches.WorldToScreenPatch.KeepCamera = true;
            try
            {
                MBWindowManager.WorldToScreen(cam, point, ref x, ref y, ref w);
            }
            finally
            {
                BannerlordVR.Patches.WorldToScreenPatch.KeepCamera = false;
            }

            if (!(w > 0f) || float.IsNaN(x) || float.IsNaN(y) || x < 0f || y < 0f ||
                x > VrUiSplit.WorldW || y > VrUiSplit.WorldH)
                return false;
            at = new Vector2(x, y);
            return true;
        }

        // TEST 628: the last few on/off flips of the laser on the map, logged.
        private static bool _wasActiveOnMap;
        private static int _flipLogs;
        private static long _flipWindow;

        // TEST 629: frames a lost palette hit is still held (one dropped native
        // frame flipped the beam white/red and parked the cursor off the money).
        private const int PaletteGraceFrames = 6;
        private static int _paletteGrace;
        private static string _flipWhy = "";
        private static bool _broken;

        public static void Tick()
        {
            if (_broken)
                return;

            try
            {
                bool panel = VrMenu.Open;
                bool wanted = Enabled && !panel && Wanted();

                bool pin = Enabled && PinMenus && !panel && PinWanted();
                if (pin != Pinned)
                {
                    Pinned = pin;

                    // TEST 400: native pins once, at whatever distance it has - the
                    // panel's own, not the map's gaze plane (TEST 398).
                    if (pin)
                        VrUiSettings.PushGeometry();
                    NativeMethods.bvr_set_ui_pin(pin ? 1 : 0);

                    // Pinned, the map's nameplates would be laid out through the
                    // head camera onto a quad that no longer follows the head.
                    SetMapNameplates(!pin);
                    if (pin && !_loggedPin)
                    {
                        _loggedPin = true;
                        VrLog.Info("Laser: a menu is open over the world, so the interface is "
                                   + "pinned in the room until it closes (TEST 378).");
                    }
                }

                bool click = SelectHand();

                Active = false;
                GroundActive = false;
                OnMapWidget = false;
                OnPalette = false;

                BvrLaser hit = default;
                bool got = wanted && NativeMethods.bvr_get_laser(out hit) == (int)BvrStatus.Ok;
                _flipWhy = got ? $"native valid {hit.Valid} target {hit.Target} hand {hit.Hand}/{Hand} at {hit.U:0.000},{hit.V:0.000}" : "no laser";
                if (got && hit.Valid != 0 && (hit.Target == 1 || hit.Target == 3) && hit.Hand == Hand)
                {
                    float w = Screen.RealScreenResolutionWidth;
                    float h = Screen.RealScreenResolutionHeight;
                    var at = new Vector2(hit.U * w, hit.V * h);

                    // TEST 649: a hit on the split's WORLD panel is taken below, where the
                    // beam's end looks to be - not here, where the beam crosses the panel.
                    if (VrMissionPhase.Deploying && !VrMissionPhase.UiMenuOpen
                        ? !VrGamepad.GroundDragging && (hit.Target == 3 || (!OnWorldPage(at) && OverDeploymentUi(at)))
                        // TEST 628: a palette hit is the bar's interface by itself - the
                        // money flickered white/red with no tooltip; the window hit test
                        // is the suspect (unproven - the flip log below says).
                        : !OnMap() || hit.Target == 3 || OverMapHud(at))
                    {
                        if (!VrGamepad.DeploymentFlying && !VrGamepad.DeploymentLift)
                            Point(at, click);
                        Active = true;
                        OnMapWidget = OnMap();
                        OnPalette = hit.Target == 3;
                        _paletteGrace = OnPalette && OnMapWidget ? PaletteGraceFrames : 0;

                        if (OnMapWidget && !_loggedMap)
                        {
                            _loggedMap = true;
                            VrLog.Info("Laser: on a campaign map widget; A clicks it, "
                                       + "and the red beam is the terrain's again off it.");
                        }
                    }
                }

                // TEST 649: "I can't press the group or machine markers with the laser
                // until I get close". They hang on the world panel (at most ~9 m away,
                // TEST 644) over troops that are often 50+ m off, and the beam leaves
                // from the HAND: aimed so its end sits on a marker, it crosses the panel
                // up to ~0.4 m (a few degrees) from where the eyes see the marker. Close
                // up, the panel is at the troops' depth and the offset vanishes. So the
                // marker is picked where the beam's END projects through the panel
                // camera the markers were laid out with; the beam keeps going to the
                // ground, its dot on the marker.
                float seenGround = 0f;
                if (!Active && wanted && VrMissionPhase.Deploying && !VrMissionPhase.UiMenuOpen &&
                    VrUiSplit.Active && !VrGamepad.GroundDragging)
                {
                    bool grounded = TryDeploymentGround(out Vec3 seen, out float seenAt, out _);
                    if ((grounded || seen != Vec3.Zero) && ProjectToWorldPage(seen, out Vector2 onPanel) &&
                        OverDeploymentUi(onPanel))
                    {
                        if (!VrGamepad.DeploymentFlying && !VrGamepad.DeploymentLift)
                            Point(onPanel, click);
                        Active = true;
                        seenGround = grounded ? Math.Max(0.01f, seenAt / VrCameraDriver.WorldScale) : -10f;
                        if (!_loggedSeen)
                        {
                            _loggedSeen = true;
                            VrLog.Info("Laser (TEST 649): a pre-battle marker picked where the beam's end looks to be "
                                       + $"({(grounded ? F(seenAt / VrCameraDriver.WorldScale) + " m away" : "in the sky")}).");
                        }
                    }
                    // Nothing there: the panel hit as before (a beam into the sky).
                    else if (got && hit.Valid != 0 && hit.Target == 1 && hit.Hand == Hand)
                    {
                        var at = new Vector2(hit.U * Screen.RealScreenResolutionWidth, hit.V * Screen.RealScreenResolutionHeight);
                        if (OnWorldPage(at) && !grounded && OverDeploymentUi(at))
                        {
                            if (!VrGamepad.DeploymentFlying && !VrGamepad.DeploymentLift)
                                Point(at, click);
                            Active = true;
                        }
                    }
                }

                // TEST 629: the cursor stays where the palette hit left it.
                if (!Active && wanted && OnMap() && _paletteGrace > 0)
                {
                    _paletteGrace--;
                    Active = OnMapWidget = OnPalette = true;
                }

                // TEST 663: off the interface a held click is a drag in progress (a
                // formation line, a portrait carried out) - never frozen when it comes back.
                if (!Active)
                {
                    _pressed |= click;
                    _slopBroken = true;
                    _haveLast = false;
                }

                if (!click)
                    _pressed = false;

                if (OnMap() && Active != _wasActiveOnMap)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - _flipWindow > 10 * System.Diagnostics.Stopwatch.Frequency)
                    {
                        _flipWindow = now;
                        _flipLogs = 0;
                    }
                    if (++_flipLogs <= 30)
                    {
                        Widget hov = null;
                        foreach (ScreenLayer l in ScreenManager.SortedLayers)
                        {
                            if (l is GauntletLayer g && g.IsActive && !g.IsFinalized && g.UIContext?.EventManager?.HoveredWidget != null)
                                hov = g.UIContext.EventManager.HoveredWidget;
                        }
                        VrLog.Info("Laser (TEST 629): on the map the laser is now " + (Active ? "on the interface" : "off it")
                                   + (Active ? (OnPalette ? " (the hand palette)" : " (the room UI)") : "") + " - " + _flipWhy
                                   + ", hovered " + (hov == null ? "nothing" : hov.GetType().Name + " '" + hov.Id + "'") + ".");
                    }
                }
                _wasActiveOnMap = OnMap() && Active;

                // The game's interface draws the beam whenever the laser is wanted,
                // except on the map off its widgets: the red beam is the
                // terrain's there.
                bool beamToUi = !OnMap() || OnMapWidget;

                float groundDistance = 0f;
                bool onGround = false;
                if (wanted && VrMissionPhase.Deploying && !VrMissionPhase.UiMenuOpen && !Active &&
                    NativeMethods.bvr_get_input_state(out BvrInputState hands) == (int)BvrStatus.Ok &&
                    (Hand == 0 ? hands.Left.IsActive : hands.Right.IsActive) != 0)
                {
                    GroundActive = true;
                    // shortcut: the beam uses this tick's hit distance with the next native pose; pass a stage hit point if fast sweeps visibly miss.
                    bool grounded = TryDeploymentGround(out Vec3 point, out float distance, out _);
                    groundDistance = grounded ? Math.Max(0.01f, distance / VrCameraDriver.WorldScale) : -10f;
                    // OrderTroopPlacer rays through the cursor whenever the mouse
                    // shows, so the cursor goes where the beam meets the ground and
                    // a held grip drags the formation line along with the laser.
                    onGround = grounded && CursorAt(point);
                }

                // A held trigger lifts the deployment camera only with the cursor
                // off the widgets, so it parks even off the palette.
                bool lift = VrMissionPhase.Deploying && VrGamepad.DeploymentLift;
                if (wanted && !VrStickMouse.Active && !VrGamepad.DeploymentFlying &&
                    (lift || ((OnMap() || GroundActive) && !Active && !onGround)))
                    ParkCursor();
                if (seenGround != 0f)
                    groundDistance = seenGround;   // TEST 649: the dot stays on the marker
                NativeMethods.bvr_set_laser(wanted ? 1 : 0, Hand, beamToUi ? 1 : 0,
                                            click ? 1 : 0, groundDistance);

                TickHover();
            }
            catch (Exception ex)
            {
                _broken = true;
                Active = false;
                GroundActive = false;
                OnMapWidget = false;
                OnPalette = false;
                VrLog.Error("Laser failed; menus keep the stick pointer and the pad.", ex);
            }
        }

        /// <summary>
        /// TEST 663: "I can't press the commanders on top" (pre-battle, the hand
        /// palette). A captain portrait is a drag source (OrderOfBattle.Hero.xml,
        /// AcceptDrag), and Gauntlet turns a press into a drag once the cursor moves
        /// 10 x sqrt(Context.Scale) px (EventManager.MouseMove) - ~5-7 px on the
        /// pre-battle layer, scaled to ~0.25-0.45. Squeezing the grip (the pre-battle
        /// click) swings the beam far more, so the portrait was dragged, never
        /// clicked; the formation cards are no drag source and clicked fine. So a
        /// held click keeps the cursor where it was pressed until the beam has moved
        /// laser_click_slop_px window pixels away - a drag still starts then (the
        /// jump is past Gauntlet's own threshold) - for every laser click (A in
        /// menus: inventory items are drag sources too).
        /// </summary>
        private static void Point(Vector2 at, bool click)
        {
            if (click && !_pressed)
            {
                _pressed = true;
                _slopBroken = false;
                _pressAt = _haveLast ? _lastAt : at;    // before the squeeze's last frame
            }

            if (click && !_slopBroken)
            {
                if ((at - _pressAt).Length() > ClickSlop)
                    _slopBroken = true;
                else
                    at = _pressAt;
            }

            Input.SetMousePosition((int)at.X, (int)at.Y);
            _lastAt = at;
            _haveLast = !click;
        }

        private static readonly float ClickSlop = VrConfig.Float("laser_click_slop_px", 48f);
        private static bool _pressed, _slopBroken, _haveLast;
        private static Vector2 _pressAt, _lastAt;

        /// <summary>The hand whose trigger was pulled last points; a pull does
        /// nothing else. Returns whether the current laser click is down.</summary>
        private static bool SelectHand()
        {
            if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                return false;

            bool l = input.Left.IsActive != 0 && input.Left.Trigger >= Threshold;
            bool r = input.Right.IsActive != 0 && input.Right.Trigger >= Threshold;

            if (OnMap() || VrMissionPhase.Deploying)
                Hand = 1;
            else if (l && !_wasLeft)
                Hand = 0;
            else if (r && !_wasRight)
                Hand = 1;

            _wasLeft = l;
            _wasRight = r;
            return input.Right.IsActive != 0 && (VrMissionPhase.Deploying
                ? NativeMethods.PhysicalGrip(input.Right) >= GripThreshold
                : (input.Right.Buttons & (int)BvrButton.Primary) != 0);
        }

        /// <summary>Never in a fight or free walking; in a mission only while a
        /// menu is up, or pre-battle to place the formations (TEST 570).</summary>
        private static bool Wanted()
        {
            if (Mission.Current != null)
                return VrMissionPhase.UiMenuOpen ||
                    (VrMissionPhase.Deploying && VrMissionPhase.VrOwnsCamera);
            return true;
        }

        private static bool PinWanted()
        {
            if (Mission.Current != null)
                return VrMissionPhase.UiMenuOpen;

            // Only the map puts the interface on its own quad outside a mission;
            // everything else is the flat screen, which is pinned anyway.
            return VrMapStereo.Live && VrStickMouse.PointerWanted();
        }

        /// <summary>The stereo campaign map, bare or with a menu pinned over it.</summary>
        private static bool OnMap() =>
            Mission.Current == null && VrMapStereo.Live;

        private static bool OverDeploymentUi(Vector2 at)
        {
            foreach (ScreenLayer layer in ScreenManager.SortedLayers)
                // The order bar too: it stays on the head panel, and its sets
                // (facing, arrangement) are picked by pointing at them.
                if (layer is GauntletLayer g && (IsDeploymentLayer(g.Name) || g.Name == "MissionOrder") &&
                    g.IsActive && !g.IsFinalized && g.HitTest(at))
                    return true;
            return false;
        }

        /// <summary>The game's cursor onto a world point, through the mission
        /// camera the placer rays through. False when it is off the screen.</summary>
        private static bool CursorAt(Vec3 point)
        {
            MatrixFrame head = VrCameraDriver.HeadFrame;
            if (VrGamepad.DeploymentFlying || VrGamepad.DeploymentLift ||
                !(ScreenManager.TopScreen is MissionScreen screen) || screen.SceneView == null ||
                Vec3.DotProduct(point - head.origin, head.rotation.f) <= 0f)
                return false;
            TaleWorlds.Library.Vec2 at = screen.SceneView.WorldPointToScreenPoint(point);
            if (at.x <= 0f || at.x >= 1f || at.y <= 0f || at.y >= 1f)
                return false;
            Input.SetMousePosition((int)(at.x * Screen.RealScreenResolutionWidth),
                                   (int)(at.y * Screen.RealScreenResolutionHeight));
            return true;
        }

        /// <summary>The order bar's Facing set is open, so a click on the ground
        /// means "face this way" (OrderTroopPlacer.IsDrawingFacing follows it).</summary>
        internal static bool FacingArmed() =>
            Mission.Current?.GetMissionBehavior<TaleWorlds.MountAndBlade.View.MissionViews.Order.OrderTroopPlacer>()?.IsDrawingFacing == true;

        /// <summary>"Face this direction" at the laser's point. The pad's path
        /// (GauntletOrderUIHandler, Input.IsGamepadActive) would instead run
        /// whichever order of the set is highlighted, so the grip never sends the
        /// game a click for it.</summary>
        internal static void FaceAtLaser()
        {
            Mission mission = Mission.Current;
            OrderController orders = mission?.PlayerTeam?.PlayerOrderController;
            if (orders == null || orders.SelectedFormations.Count == 0 ||
                !TryDeploymentGround(out Vec3 point, out _, out _))
                return;
            if (mission.IsNavalBattle)
                point.z = mission.Scene.GetWaterLevelAtPosition(point.AsVec2, true, true);
            orders.SetOrderWithPosition(OrderType.LookAtDirection,
                                        new WorldPosition(mission.Scene, UIntPtr.Zero, point, false));
            VrLog.Info($"Deployment: right grip faced {orders.SelectedFormations.Count} formation(s) toward the laser point.");
            NativeMethods.bvr_trigger_haptic(1, 0.3f, 0.025f);
        }

        /// <summary>The pre-battle screen: the land one or War Sails'.</summary>
        internal static bool IsDeploymentLayer(string name) =>
            name == "MissionOrderOfBattle" || name == "NavalOrderOfBattle";

        /// <summary>Along a ray to the sea surface, in a War Sails battle.</summary>
        internal static bool WaterDistance(Mission mission, Vec3 origin, Vec3 dir, out float t)
        {
            t = 0f;
            if (mission?.Scene == null || !mission.IsNavalBattle || dir.z > -1e-3f)
                return false;
            // ponytail: three fixed-point steps over the wave height; fine unless
            // the ray grazes the sea almost flat.
            for (int i = 0; i < 3; i++)
            {
                float level = mission.Scene.GetWaterLevelAtPosition((origin + dir * t).AsVec2, true, true);
                t = Math.Max(0f, (origin.z - level) / -dir.z);
            }
            return true;
        }

        /// <summary>
        /// TEST 651: "add smoothing to the laser". Native smooths the beam (one-euro,
        /// laser_smooth_hz / _beta) but drew it RAW pre-battle, because this ray -
        /// the ground point, the marker pick, the cursor - was cast from the raw aim
        /// and the two had to agree. Now native smooths always and hands its drawn
        /// direction back; the cast follows the beam you see. Raw if native has none.
        /// </summary>
        private static Vec3 DrawnDir(int hand, MatrixFrame aim) =>
            DrawnDir(hand, aim.rotation.f, p => VrCameraDriver.StageToWorld(Level, p).origin);

        /// <summary>The same for any space: `toWorld` maps a stage point (the
        /// campaign map's table has its own).</summary>
        internal static Vec3 DrawnDir(int hand, Vec3 raw, Func<BvrVec3, Vec3> toWorld)
        {
            if (NativeMethods.bvr_get_laser_ray(hand, out BvrVec3 o, out BvrVec3 d) != (int)BvrStatus.Ok)
                return raw;
            Vec3 w = toWorld(new BvrVec3 { X = o.X + d.X, Y = o.Y + d.Y, Z = o.Z + d.Z }) - toWorld(o);
            return w.LengthSquared > 1e-8f ? w.NormalizedCopy() : raw;
        }

        internal static readonly BvrQuat Level = new BvrQuat { W = 1f };

        internal static bool TryDeploymentGround(out Vec3 point, out float distance,
                                                  out WeakGameEntity entity)
        {
            point = Vec3.Zero;
            distance = 0f;
            entity = WeakGameEntity.Invalid;
            Mission mission = Mission.Current;
            if (mission?.Scene == null ||
                NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                return false;

            BvrHandState hand = Hand == 0 ? input.Left : input.Right;
            if (hand.IsActive == 0)
                return false;

            MatrixFrame aim = VrCameraDriver.StageToWorld(hand.AimOrientation, hand.AimPosition);
            Vec3 dir = DrawnDir(Hand, aim);
            Vec3 end = aim.origin + dir * (1000f * VrCameraDriver.WorldScale);
            bool hit = mission.Scene.RayCastForClosestEntityOrTerrain(aim.origin, end, out distance,
                    out entity, 0.3f, (BodyFlags)67188481);

            // War Sails places on the WATER (NavalOrderTroopPlacer projects the
            // cursor onto it), so the sea surface stops the ray before the seabed.
            if (WaterDistance(mission, aim.origin, dir, out float t))
            {
                if (!hit || t < distance)
                {
                    distance = t;
                    entity = WeakGameEntity.Invalid;
                    hit = true;
                }
            }
            if (!hit)
            {
                // TEST 649: a point far along the ray (300 m) for the marker pick - a
                // siege marker hangs 8 m up, often against the sky. False all the same.
                point = aim.origin + dir * (300f * VrCameraDriver.WorldScale);
                return false;
            }
            point = aim.origin + dir * distance;
            return true;
        }

        /// <summary>A click through the world selects the formation under the
        /// right-hand ray through Bannerlord's own order-of-battle callback.</summary>
        internal static bool TrySelectGroundFormation()
        {
            Mission mission = Mission.Current;
            var find = OrderOfBattleFormationItemVM.GetFormationWithCondition;
            var select = OrderOfBattleFormationItemVM.OnSelection;
            bool naval = mission?.IsNavalBattle == true;
            if (!VrMissionPhase.Deploying || mission?.PlayerTeam == null || (!naval && (find == null || select == null)) ||
                NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok ||
                input.Right.IsActive == 0)
                return false;

            MatrixFrame aim = VrCameraDriver.StageToWorld(input.Right.AimOrientation, input.Right.AimPosition);
            Vec3 origin = aim.origin, direction = DrawnDir(1, aim);
            float reach = TryDeploymentGround(out _, out float terrain, out _) ? terrain + 2f : 200f;
            Agent nearest = null;
            float best = float.MaxValue;
            foreach (Agent agent in mission.Agents)
            {
                if (agent == null || !agent.IsActive() || agent.Team != mission.PlayerTeam || agent.Formation == null)
                    continue;

                Vec3 to = VrPhysicalCombat.Chest(agent) - origin;
                float along = Vec3.DotProduct(to, direction);
                if (along <= 0f || along > reach)
                    continue;
                float sideways = (to - direction * along).LengthSquared;
                float radius = 0.5f + along * 0.025f;
                if (sideways < radius * radius && sideways < best)
                {
                    nearest = agent;
                    best = sideways;
                }
            }

            if (nearest == null)
                return false;

            // Already selected: the press is the start of its line, not a pick
            // ("hold grip and swipe, the army stays still").
            if (mission.PlayerTeam.PlayerOrderController?.SelectedFormations.Contains(nearest.Formation) == true)
                return false;

            // War Sails has its own view model, which follows the order
            // controller's selection (NavalOrderOfBattleVM.OnSelectedFormationsChanged);
            // the land statics above may still point at a finished land battle.
            if (naval)
            {
                OrderController orders = mission.PlayerTeam.PlayerOrderController;
                if (orders == null)
                    return false;
                orders.ClearSelectedFormations();
                orders.SelectFormation(nearest.Formation);
                VrLog.Info($"Deployment (War Sails): right grip selected formation {nearest.Formation.Index + 1} from the battlefield.");
                NativeMethods.bvr_trigger_haptic(1, 0.3f, 0.025f);
                return true;
            }

            OrderOfBattleFormationItemVM item = find(f => f.Formation == nearest.Formation).FirstOrDefault();
            if (item == null || !item.IsSelectable)
                return false;
            select(item);
            VrLog.Info($"Deployment: right grip selected formation {item.Formation.Index + 1} from the battlefield.");
            NativeMethods.bvr_trigger_haptic(1, 0.3f, 0.025f);
            return true;
        }

        /// <summary>TEST 400: the settlement and party nameplates, shown or faded
        /// out. Missions have no such layer.</summary>
        private static void SetMapNameplates(bool visible)
        {
            List<ScreenLayer> layers = ScreenManager.SortedLayers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i] is GauntletLayer g && g.Name == "MapNameplateLayer" && g.UIContext != null)
                    g.UIContext.ContextAlpha = visible ? 1f : 0f;
            }
        }

        /// <summary>Is the point on a widget of any layer but the nameplates?</summary>
        private static bool OverMapHud(Vector2 at) => OverWidget(at, false);

        private static bool OverWidget(Vector2 at, bool nameplates)
        {
            List<ScreenLayer> layers = ScreenManager.SortedLayers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i] is GauntletLayer g && g.IsActive && !g.IsFinalized &&
                    (nameplates || g.Name != "MapNameplateLayer") && g.HitTest(at))
                    return true;
            }
            return false;
        }

        // --- TEST 529: the cursor off the widgets -----------------------------
        //
        // ScreenManager counts A (the pad's confirm key) as a MOUSE button, and the
        // layer under the game's cursor takes it. The laser moves that cursor onto
        // a palette button and, off it, used to leave it there - so A at the ground
        // went to the palette's layer, not the map ("I need to press a button to
        // move"). Off a widget on the map the cursor now sits on a spot no widget
        // covers (screen fractions, tried in order), and A falls through to the map.

        // TEST 530: the mouse is visible on the map now, so the top edge first -
        // out of the way at the edge of the view - and the middle only as a last resort.
        private static readonly float[] Spots =
            { 0.5f, 0.01f,  0.3f, 0.01f,  0.7f, 0.01f,  0.01f, 0.5f,  0.99f, 0.5f,  0.5f, 0.5f,  0.5f, 0.3f };

        private static Vector2 _parked = new Vector2(-1f, -1f);
        private static bool _loggedPark;

        private static void ParkCursor()
        {
            bool deployment = VrMissionPhase.Deploying && !VrMissionPhase.UiMenuOpen;
            if (_parked.X < 0f || (deployment ? OverDeploymentUi(_parked) : OverWidget(_parked, true)))
            {
                float w = Screen.RealScreenResolutionWidth;
                float h = Screen.RealScreenResolutionHeight;
                _parked = new Vector2(-1f, -1f);
                for (int i = 0; i < Spots.Length; i += 2)
                {
                    var at = new Vector2(Spots[i] * w, Spots[i + 1] * h);
                    if (!(deployment ? OverDeploymentUi(at) : OverWidget(at, true)))
                    {
                        _parked = at;
                        break;
                    }
                }

                if (!_loggedPark)
                {
                    _loggedPark = true;
                    VrLog.Info(_parked.X < 0f
                        ? "Laser (TEST 529): no spot free of widgets to park the cursor; A stays the interface's."
                        : $"Laser (TEST 529): off the widgets the cursor waits at ({_parked.X:0}, {_parked.Y:0}), "
                          + "so A on the ground reaches the world.");
                }
            }

            if (_parked.X >= 0f)
                Input.SetMousePosition((int)_parked.X, (int)_parked.Y);
        }

        /// <summary>A short tick in the pointing hand each time a new widget is
        /// hovered, so a button can be found without looking at the dot.</summary>
        private static void TickHover()
        {
            Widget hovered = null;
            if (Active)
            {
                List<ScreenLayer> layers = ScreenManager.SortedLayers;
                for (int i = layers.Count - 1; i >= 0 && hovered == null; i--)
                {
                    if (layers[i] is GauntletLayer g && g.IsActive && !g.IsFinalized)
                        hovered = g.UIContext?.EventManager?.HoveredWidget;
                }
            }

            if (hovered != null && !ReferenceEquals(hovered, _hovered))
                NativeMethods.bvr_trigger_haptic(Hand, 0.15f, 0.01f);
            _hovered = hovered;
        }
    }
}
