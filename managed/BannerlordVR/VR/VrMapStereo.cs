using System;
using BannerlordVR.Interop;
using HarmonyLib;
using SandBox.View.Map;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The campaign map in stereo (TEST 273), with a controller laser for clicks (TEST 274).
    ///
    /// The map's own scene view is pointed at eye texture 0 and its camera is swung
    /// between the eyes on alternate frames - AFR, the same thing a battle does,
    /// but through the map's SceneLayer instead of a MissionScreen. The map is a
    /// tabletop model (TEST 275): the game camera's focus sits on a table in the
    /// room at a fixed scale, the game's zoom raises and lowers the viewer, and
    /// the head is fully 6DoF. The red laser shows while A is held.
    ///
    /// THE BINDING IS NEVER UNDONE. SetRenderTarget(null) throws, and standing a
    /// retargeted view down freezes the window rather than restoring it (see
    /// VrMissionPhase). The game disables the map view itself whenever anything
    /// is on top of the map - a battle, the inventory, a conversation - so the
    /// view sharing eye texture 0 with a battle is safe, and the flat screen
    /// covers those moments. map_stereo therefore takes effect at the next map load.
    /// </summary>
    public static class VrMapStereo
    {
        private static bool Enabled = VrConfig.Bool("map_stereo", false);

        /// <summary>The VR panel's "Campaign map in VR" checkbox. It only becomes
        /// <see cref="Enabled"/> once no map is bound to the eye texture - the
        /// binding cannot be undone, so switching off under a bound map would
        /// freeze the flat screen.</summary>
        public static bool Wanted { get; private set; } = Enabled;

        public static void SetEnabled(bool on) => Wanted = on;

        /// <summary>Map units per metre of table. FIXED: the user did not want the
        /// model to change size with the game's zoom (TEST 277). 15 is about what
        /// the flat view shows at its usual camera distance.</summary>
        private static readonly float TableScale = Clamp(VrConfig.Float("map_table_scale", 15f), 1f, 1000f);

        private static readonly bool Laser = VrConfig.Bool("map_laser", true);

        /// <summary>
        /// Aim the map camera at the OTHER eye than native labels. The map renders
        /// one frame behind the label (unlike a mission), so without this the pair
        /// arrives swapped - pseudo-stereo, "cross-eyed". TEST 277 confirmed.
        /// </summary>
        private static readonly bool SwapEyes = VrConfig.Bool("map_swap_eyes", true);

        /// <summary>Follow stereo_mode = nativestereo on the map (TEST 286). 0 keeps
        /// the map on AFR whatever the battle mode is.</summary>
        private static readonly bool NativeStereo = VrConfig.Bool("map_native_stereo", true);

        private static readonly bool StereoDupAllowed = VrConfig.Bool("stereo_dup", true);

        private static bool _loggedNative;

        /// <summary>The game camera distance when the map came up; zooming away
        /// from it lifts or lowers the viewer at the same scale.</summary>
        private static float _baseDistance = -1f;

        private static readonly bool StickPan = VrConfig.Bool("map_stick_pan", true);

        /// <summary>TEST 343: the game's depth of field on the table (off).</summary>
        private static readonly bool Dof = VrConfig.Bool("map_dof", false);

        /// <summary>
        /// TEST 399: how close the grip zoom may bring the STANDING eye to the
        /// ground under it, room metres. The game's own zoom (the bumpers, our
        /// grips) is floored there, so the descent eases to a stop instead of
        /// sinking through the terrain; leaning in and crouching stay free.
        /// </summary>
        private static readonly float MinEyeM = Clamp(VrConfig.Float("map_min_eye_height_m", 0.15f), 0.02f, 2f);

        // TargetCameraDistance is protected virtual; virtual calls honour subclasses.
        // Null if a game update renames it: the floor then stands down, not the map.
        private static readonly Func<MapCameraView, float> GetTargetDistance =
            DelegateOrNull<Func<MapCameraView, float>>(
                AccessTools.PropertyGetter(typeof(MapCameraView), "TargetCameraDistance"));

        private static readonly Action<MapCameraView, float> SetTargetDistance =
            DelegateOrNull<Action<MapCameraView, float>>(
                AccessTools.PropertySetter(typeof(MapCameraView), "TargetCameraDistance"));

        private static T DelegateOrNull<T>(System.Reflection.MethodInfo method) where T : Delegate
        {
            try
            {
                return method == null ? null : AccessTools.MethodDelegate<T>(method);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>TEST 398: the nearest the interface plane comes on the map, metres.</summary>
        private static readonly float UiNearM = Clamp(VrConfig.Float("map_ui_near_m", 0.3f), 0.3f, 5f);

        /// <summary>
        /// TEST 398: how far ahead the head is looking on the table, metres,
        /// smoothed; 0 when the map is not live. The per-eye interface plane is
        /// hung here (VrUiProjection): a label at the plane's own depth has no
        /// disparity on it, so neither the half-rate eye alternation nor a
        /// pairing slip can move or double it - "doubled when I get close" was
        /// a 2.47 m plane with labels 0.3 m away.
        /// </summary>
        public static float UiDepthMetres { get; private set; }

        /// <summary>The left stick is the table's pan: stereo map up, no pointer.</summary>
        public static bool StickPans => Live && StickPan && !VrStickMouse.Active;

        /// <summary>
        /// MapCameraView.OnBeforeTick prefix. On a pad the game pans with the left
        /// stick only in its own cursor mode, and then along the GAME camera's
        /// heading. At a table "forward" is the way you face, so the stick is
        /// turned by the head's heading relative to the table and handed to the
        /// game's own pan (RX right, RY forward along the camera = the table).
        /// </summary>
        public static void SteerPan(ref MapCameraView.InputInformation input)
        {
            // No zoom-to-cursor on the table (TEST 279: "camera flies across the
            // map"). The game pans so the point under the cursor stays put while a
            // zoom eases in, measuring the cursor ray from ITS camera - but on the
            // table that ray starts at the hand or the head, nowhere near it, so
            // every zoom step shoved the camera sideways.
            if (Live)
                input.RayCastForClosestEntityOrTerrainCondition = false;

            // TEST 282: "left stick doesn't move my camera". Counted so the next
            // log says which gate held it rather than leaving it to a guess.
            bool moved = VrGamepad.TryGetStick(false, out float x, out float y)
                         && (x != 0f || y != 0f);
            if (moved)
            {
                if (!Live) _panNotLive++;
                else if (VrStickMouse.Active) _panPointer++;
                else if (!StickPan) _panOff++;
            }

            if (!StickPans || !moved)
                return;

            // TEST 528: nothing moves the table while a town's UI is up.
            if (_holding)
            {
                _panHeld++;
                return;
            }

            Vec2 d = VrCameraDriver.MapStickToTable(x, y);
            input.RX = d.x * input.RS;
            input.RY = d.y * input.RS;
            _panApplied++;
        }

        private static int _panApplied, _panNotLive, _panPointer, _panOff, _panHeld;

        // --- TEST 526: no camera fly at settlements ----------------------------
        //
        // Entering a town, village, castle or any game menu puts the camera in
        // FollowParty (MapScreen.EnterMenuContext), and FollowParty retargets
        // IdealCameraTarget to the SETTLEMENT every tick (the midpoint with the port
        // for a port town); OnBeforeTick then glides the table there. Held here
        // instead: the target is pinned where the camera already is and the mode
        // kept Free, so GetMapCameraInput never retargets. TEST 528: and the table
        // stays put until the town's UI closes ("camera moves when I press grip or
        // stick"): stick pan, grip zoom and stick turn are dropped while held.
        // On leaving, vanilla's own after-menu state: FollowParty.

        private static readonly bool HoldOn = VrConfig.Bool("map_hold_at_settlement", true);

        // Protected virtual; null after a game update renames one, and the hold then
        // stands down (logged once) - the map itself is unaffected.
        private static readonly Action<MapCameraView, Vec3> SetIdealTarget =
            DelegateOrNull<Action<MapCameraView, Vec3>>(
                AccessTools.PropertySetter(typeof(MapCameraView), "IdealCameraTarget"));

        private static readonly Action<MapCameraView, MapCameraView.CameraFollowMode> SetFollowMode =
            DelegateOrNull<Action<MapCameraView, MapCameraView.CameraFollowMode>>(
                AccessTools.PropertySetter(typeof(MapCameraView), "CurrentCameraFollowMode"));

        private static readonly AccessTools.FieldRef<MapCameraView, Vec3> CameraTarget = CameraTargetOrNull();

        private static AccessTools.FieldRef<MapCameraView, Vec3> CameraTargetOrNull()
        {
            try
            {
                return AccessTools.FieldRefAccess<MapCameraView, Vec3>("_cameraTarget");
            }
            catch
            {
                return null;
            }
        }

        // --- TEST 530: the laser is the map's cursor, no B needed ---------------
        //
        // On a pad the map starts in its own cursor mode (_mapSceneCursorWanted,
        // toggled by MapChangeCursorMode = B): a target decal at the screen centre,
        // and MapCursor.SetVisible(true) -> MapScreen.SetMouseVisible(false). No
        // other layer on the bare map asks for the mouse, so the engine hides it
        // and the laser's cursor reached neither the palette nor the ground until
        // B switched the mode off. In VR the laser IS the map cursor (TryLaser), so
        // the mode is held off while the table is live. Called from the
        // MapCameraView.OnBeforeTick prefix, after MapScreen worked out the mode for
        // this frame and before it applies it (MapCursor.SetVisible).

        private static readonly AccessTools.FieldRef<MapScreen, bool> SceneCursorWanted = SceneCursorWantedOrNull();

        private static AccessTools.FieldRef<MapScreen, bool> SceneCursorWantedOrNull()
        {
            try
            {
                return AccessTools.FieldRefAccess<MapScreen, bool>("_mapSceneCursorWanted");
            }
            catch
            {
                return null;
            }
        }

        private static bool _loggedCursor;

        public static void LaserOwnsCursor()
        {
            MapScreen map = MapScreen.Instance;
            if (!Live || !Laser || map == null)
                return;

            if (SceneCursorWanted != null)
                SceneCursorWanted(map) = false;
            map.MapSceneCursorActive = false;

            if (!_loggedCursor)
            {
                _loggedCursor = true;
                VrLog.Info(SceneCursorWanted == null
                    ? "Map cursor (TEST 530): MapScreen._mapSceneCursorWanted is gone (a game update?); "
                      + "the mode is held off frame by frame only."
                    : "Map cursor (TEST 530): the map's pad cursor mode is held off - the laser is the "
                      + "cursor and the mouse stays live, so no B to switch.");
            }
        }

        private static bool _holding;
        private static bool _loggedHoldBroken;
        private static int _fastMovesBlocked;

        /// <summary>MapCameraView.OnBeforeTick prefix, before <see cref="SteerPan"/>.
        /// Engages only on the live table; once engaged it lasts while the party is
        /// at the settlement or a menu is open, even through a screen over the map.</summary>
        public static void HoldAtSettlement(MapCameraView mcv, ref MapCameraView.InputInformation input)
        {
            bool atSettlement = MapScreen.Instance?.IsInMenu == true
                                || MobileParty.MainParty?.CurrentSettlement != null;
            bool hold = HoldOn && mcv != null && atSettlement && (Live || _holding);

            if (hold && (SetIdealTarget == null || SetFollowMode == null || CameraTarget == null))
            {
                hold = false;
                if (!_loggedHoldBroken)
                {
                    _loggedHoldBroken = true;
                    VrLog.Warn("Map camera hold (TEST 526): MapCameraView's IdealCameraTarget, "
                               + "CurrentCameraFollowMode or _cameraTarget is gone (a game update?); "
                               + "the table glides to settlements as in vanilla.");
                }
            }

            if (hold)
            {
                if (!_holding)
                {
                    SetIdealTarget(mcv, CameraTarget(mcv));
                    VrLog.Info("Map camera held at a settlement (TEST 526): the table stays where it "
                               + "was instead of gliding to the settlement.");
                }
                SetFollowMode(mcv, MapCameraView.CameraFollowMode.Free);

                // TEST 528: no pan (stick, or the game's own), zoom (grips =
                // bumpers) or turn (right stick, rotate keys, right mouse).
                input.RX = input.RY = 0f;
                input.MapZoomIn = input.MapZoomOut = 0f;
                input.HorizontalCameraInput = 0f;
                input.RotateLeftKeyDown = input.RotateRightKeyDown = false;
                input.RightMouseButtonDown = input.MiddleMouseButtonDown = false;
                input.DeltaMouseScroll = 0f;
            }
            else if (_holding && mcv != null)
            {
                mcv.SetCameraMode(MapCameraView.CameraFollowMode.FollowParty);
            }

            _holding = hold;
        }

        /// <summary>TEST 526: MapCameraView.FastMoveCameraToPosition prefix - false
        /// skips it. A nameplate click flies the table there and zooms to 15; on the
        /// table neither is wanted. FastMoveCameraToMainParty (the time bar's "back
        /// to my party") is left alone.</summary>
        public static bool AllowFastMove()
        {
            if (!Live)
                return true;
            _fastMovesBlocked++;
            return false;
        }

        private static void ReportPan()
        {
            if (_panApplied + _panNotLive + _panPointer + _panOff + _panHeld == 0)
                return;

            VrLog.Info($"Map stick pan: {_panApplied} frame(s) panned with the left stick; "
                       + $"held back {_panPointer} by the stick mouse (a menu wanted the "
                       + $"pointer), {_panNotLive} with the map not live, {_panOff} by "
                       + $"map_stick_pan = 0, {_panHeld} at a settlement (TEST 528).");
            _panApplied = _panNotLive = _panPointer = _panOff = _panHeld = 0;
        }

        private static readonly AccessTools.FieldRef<MapScreen, bool> SceneEnabled =
            AccessTools.FieldRefAccess<MapScreen, bool>("_isSceneViewEnabled");

        /// <summary>True while the map is what the headset shows in stereo.</summary>
        public static bool Live { get; private set; }

        private static MapScreen _boundScreen;
        private static SceneView _boundView;
        private static Texture _boundTarget;
        private static float _appliedScale = -1f;
        private static bool _recentre;
        private static MapScreen _placedFor;
        private static bool _failed;
        private static int _frames;
        private static int _lasers;
        private static int _logTick;

        /// <summary>Application tick, before VrMissionPhase reads <see cref="Live"/>.</summary>
        public static void Tick()
        {
            bool live = false;

            // MapScreen.Instance is nulled in OnFinalize, so a bound map that is
            // merely under a battle or a menu still holds the change back.
            if (Wanted != Enabled
                && (_boundScreen == null || !ReferenceEquals(MapScreen.Instance, _boundScreen)))
            {
                Enabled = Wanted;
                VrLog.Info("Map stereo: the campaign map in VR is now " + (Enabled ? "ON" : "off") + ".");
            }

            if (Enabled && !_failed && VrSystem.IsActive && VrSystem.IsStereoReady
                && VrStereoRenderer.TexturesReady)
            {
                MapScreen map = ScreenManager.TopScreen as MapScreen;
                live = map != null && ReferenceEquals(map, _boundScreen) && SceneEnabled(map);
            }

            if (live && !Live)
            {
                // TEST 626: back from the smithy, the inventory or a battle the table
                // stays where it was (and its zoom); only a new map (a loaded save)
                // or an explicit recentre (F10, the panel) places it again.
                if (!ReferenceEquals(_placedFor, _boundScreen))
                {
                    _recentre = true;
                    _placedFor = _boundScreen;
                }

                // RE-BIND EVERY TIME THE MAP COMES BACK (TEST 282). After a battle
                // the engine had pointed the map's view back at the window while our
                // view and texture objects were unchanged, so EnsureBound saw
                // nothing to do: the map drew into the flat UI panel over the
                // battle's last frames. A resolution Apply "fixed" it only because
                // new textures forced the re-bind this now does unconditionally.
                _boundTarget = null;

                VrLog.Info("Map stereo: the campaign map is in the headset in 3D.");
            }
            else if (!live && Live)
            {
                VrLog.Info("Map stereo: the map view is off (a screen or conversation is "
                           + "over it); back on the flat screen.");
            }

            Live = live;
            if (!live)
            {
                VrCameraDriver.MapScale = 0f;
                UiDepthMetres = 0f;
                HideBeam();

                // A battle publishes eyes in metres.
                SetStereoScale(1f);
            }

            // TEST 287: THE MAP'S TICK CAN STOP WHILE ITS SCENE KEEPS DRAWING -
            // holding both triggers for the VR panel did it: no "frames driven",
            // the eye label froze, AFR parity "90 did NOT alternate", the right
            // eye seconds stale. Two app ticks in a row without the BeforeTick
            // postfix, and the app tick drives the frame itself.
            if (live && !_driven)
            {
                if (++_undriven >= 2)
                    AfterCameraUpdate(_boundScreen);
            }
            else
            {
                _undriven = 0;
            }
            _driven = false;
        }

        private static bool _driven;
        private static int _undriven;
        private static float _stereoScale = -1f;

        private static void SetStereoScale(float scale)
        {
            if (Math.Abs(scale - _stereoScale) < 1e-3f)
                return;

            _stereoScale = scale;
            NativeMethods.bvr_set_stereo_world_scale(scale);
        }

        /// <summary>
        /// MapScreen's BeforeTick postfix: the game has just rebuilt its camera, so
        /// this is the last write before the map renders. Binds on the first frame
        /// the view is ready, which is also what makes <see cref="Live"/> possible.
        /// </summary>
        public static void AfterCameraUpdate(MapScreen screen)
        {
            if (!Enabled || _failed || !VrSystem.IsActive || screen == null)
                return;

            _driven = true;

            try
            {
                SceneView view = screen.SceneLayer?.SceneView;
                Camera cam = screen.MapCameraView?.Camera;
                if (view == null || cam == null || !EnsureBound(screen, view))
                    return;

                if (!Live)
                    return;

                NativeMethods.bvr_prepare_frame();

                // The table centre is the point the game camera looks at:
                // ComputeMapCamera puts the camera at target + u * (distance + 2)
                // and looks down -u. The heading is the game camera's, so turning
                // the game camera turns the table.
                MapCameraView mcv = screen.MapCameraView;
                MatrixFrame camFrame = mcv.CameraFrame;
                float distance = mcv.CameraDistance;

                if (_recentre || _baseDistance < 0f)
                    _baseDistance = distance;

                // Zoom keeps the scale and moves you instead: zooming out raises
                // the point that sits at table height, so the map drops away below
                // you; zooming in brings it up towards you.
                MatrixFrame table = default;
                table.origin = camFrame.origin - camFrame.rotation.u * (distance + 2f);
                FloorZoom(mcv, table.origin.z);
                table.origin.z += distance - _baseDistance;
                table.rotation = VrMath.YawOnly(camFrame.rotation);

                // NO DEPTH OF FIELD ON THE TABLE (TEST 343). MapCameraView sets it
                // every tick from the game camera's PITCH - the far end of the sharp
                // range is (1 - f.z + 0.15) * 1000 units, ~150 looking at the
                // horizon, ~1150 looking down - and the right stick's vertical axis
                // is that pitch (CameraAxisY -> AdditionalElevation). The table
                // never follows the pitch, so all the stick did was blur and
                // sharpen the far map: "the right stick changes the render
                // distance". The game's own off call (MapCameraView, 821-823).
                if (!Dof && screen.MapScene != null)
                {
                    screen.MapScene.SetDepthOfFieldFocus(0f);
                    screen.MapScene.SetDepthOfFieldParameters(0f, 0f, false);
                }

                if (!VrCameraDriver.UpdateMap(table, TableScale, cam.Far, _recentre))
                    return;
                _recentre = false;

                UpdateUiDepth(view);
                MotionProbe(table, distance);

                // NATIVE STEREO (TEST 286) when that is the selected mode: the camera
                // is pinned to the left eye and native re-issues every view-dependent
                // draw for the right one in the SAME frame, offset by the published
                // eye pair - so no eye alternates, and the map's one-frame label
                // offset (SwapEyes) has nothing to swap. Otherwise AFR. DR stays off
                // on the map: its warp is unverified at table scale.
                bool native = NativeStereo && StereoDupAllowed && VrRenderMode.NativeStereoActive;

                int eye;
                int aim;
                if (native)
                {
                    NativeMethods.bvr_pin_afr_eye(0);
                    eye = 0;
                    aim = 0;
                }
                else
                {
                    NativeMethods.bvr_pin_afr_eye(-1);
                    eye = NativeMethods.bvr_next_afr_eye();

                    // TEST 277: the map renders a frame after the label, so AFR aims
                    // at the OTHER eye (pseudo-stereo, "cross-eyed", otherwise).
                    aim = SwapEyes ? 1 - eye : eye;
                }

                // Pose, projection, pose - see VrAfrRenderer.CullRefit. The game sets
                // its own 40 degree frustum on this camera every tick, so it is
                // rewritten every tick too, and that write is where the engine fits
                // the frustum it culls trees, towns and parties with. Projection
                // first fitted it on the game's camera looking down at the table, so
                // everything but the GPU-culled terrain went missing as the head
                // turned (TEST 397).
                if (VrAfrRenderer.CullRefit)
                    VrCameraDriver.AimAtEye(cam, aim);
                VrCameraDriver.ApplyEyeProjectionTo(cam, aim);
                VrCameraDriver.AimAtEye(cam, aim);

                // The view takes the camera's state when it is SET, not when it
                // renders: without this it drew the game's own camera, which is
                // what TEST 273 showed - no head tracking, no stereo, and the
                // game's 40 degree lens stretched across the headset.
                view.SetCamera(cam);

                // Native stereo's inputs: both eyes' poses, published while the
                // camera is still ON the rendered eye (the refPos native checks).
                if (native)
                {
                    // TEST 287: native's IPD sanity bounds are metres; on the table
                    // the eyes are ~15 units apart per metre, and every published
                    // pair was rejected - the right eye never existed.
                    SetStereoScale(VrCameraDriver.MapScale);
                    VrVpPatch.Publish(cam, aim);
                }

                if (native != _loggedNative)
                {
                    _loggedNative = native;
                    VrLog.Info(native
                        ? "Map stereo: NATIVE STEREO - both eyes every frame, camera pinned "
                          + "to the left eye, the right re-issued by native."
                        : "Map stereo: AFR - one eye a frame, alternating.");
                }

                // ...and now the view has its copy, park the camera object at the
                // HEAD CENTRE. The game's labels - settlement and party names,
                // banners, event icons - project through this same object
                // (MBWindowManager.WorldToScreen on MapCameraView.Camera). Left on
                // the eye it alternated every frame, so every label drew in two
                // places: TEST 280, "names and banners fixed double".
                MatrixFrame head = VrCameraDriver.HeadFrame;
                cam.LookAt(head.origin, head.origin + head.rotation.f, head.rotation.u);

                UpdateBeam(view);

                NativeMethods.bvr_set_afr_eye(eye);
                NativeMethods.bvr_set_draw_stereo(0);
                NativeMethods.bvr_set_native_stereo(native ? 1 : 0);
                NativeMethods.bvr_set_sim_generation(-1);
                NativeMethods.bvr_set_depth_reproject(0, 0f, 0f, 0f, 0f);

                _frames++;
                if (++_logTick >= 450)
                {
                    VrLog.Info($"Map stereo: {_frames} frame(s) driven in ~5 s, "
                               + $"{_lasers} laser ray(s) given to the map; UI plane at "
                               + $"{UiDepthMetres:F2} m (TEST 398), set by the nameplates on "
                               + $"{_depthFromLabels} frame(s) ({_labelVotes / Math.Max(_depthFromLabels, 1)} "
                               + $"on the table per frame) and the terrain ray on {_depthFromRay} "
                               + $"(TEST 402/403); zoom held at the floor on {_floorTicks} tick(s) (TEST 399); "
                               + $"{_fastMovesBlocked} camera fly(s) blocked, held at a settlement: "
                               + $"{(_holding ? "yes" : "no")} (TEST 526).");
                    _labelVotes = 0;
                    _fastMovesBlocked = 0;
                    ReportPan();
                    _logTick = 0;
                    _frames = 0;
                    _lasers = 0;
                    _floorTicks = 0;
                    _depthFromLabels = _depthFromRay = 0;
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Live = false;
                VrCameraDriver.MapScale = 0f;
                VrLog.Error("Map stereo failed; the map goes back to the flat screen for "
                            + "this session.", ex);
            }
        }

        /// <summary>
        /// TEST 399: "going down with the grip goes underneath the map". The lift
        /// is distance - _baseDistance, so the eye's height is linear in the game's
        /// zoom distance and the lowest distance that keeps the standing eye MinEyeM
        /// above the ground under it is solved directly. The game's TARGET is
        /// floored there, so its own easing brings the descent to a stop and zooming
        /// out answers at once - nothing accumulates below the floor. Panning onto a
        /// mountain raises the floor and the map eases up rather than through you.
        /// </summary>
        private static void FloorZoom(MapCameraView mcv, float focusZ)
        {
            // MapScale is 0 on the first live frame: no head on the table yet.
            if (VrCameraDriver.MapScale <= 0f || GetTargetDistance == null || SetTargetDistance == null)
                return;

            // The head's x,y do not depend on the lift; last frame's will do.
            if (!Ground(VrCameraDriver.HeadFrame.origin.AsVec2, out float ground))
                return;

            float floor = _baseDistance + ground - focusZ
                          + (MinEyeM - VrCameraDriver.MapEyeAboveTable) * TableScale;

            if (GetTargetDistance(mcv) >= floor)
                return;

            SetTargetDistance(mcv, floor);
            _floorTicks++;

            if (!_loggedFloor)
            {
                _loggedFloor = true;
                VrLog.Info($"Map zoom floor (TEST 399): the zoom stops with the standing eye "
                           + $"{MinEyeM:F2} m above the ground (map_min_eye_height_m).");
            }
        }

        private static int _floorTicks;
        private static bool _loggedFloor;

        /// <summary>The surface under a map point, land or sea: the flags only pick
        /// which bodies are excluded, so the higher answer is the one you would hit.</summary>
        private static bool Ground(Vec2 xy, out float height)
        {
            height = float.MinValue;
            var scene = Campaign.Current?.MapSceneWrapper;
            if (scene == null)
                return false;

            float h = 0f;
            if (scene.GetHeightAtPoint(new CampaignVec2(xy, true), ref h))
                height = h;

            h = 0f;
            if (scene.GetHeightAtPoint(new CampaignVec2(xy, false), ref h))
                height = Math.Max(height, h);

            return height > float.MinValue;
        }

        /// <summary>
        /// TEST 402: the half-angle of the cone round the head's forward whose
        /// nameplates set the plane. Wide, because the eyes look at a table well
        /// below where the head points.
        /// </summary>
        private static readonly float LabelConeCos =
            (float)Math.Cos(Clamp(VrConfig.Float("map_ui_label_cone_deg", 35f), 5f, 80f) * Math.PI / 180.0);

        private static readonly object LabelLock = new object();
        private static double _labelDepthSum, _labelWeightSum;
        private static int _depthFromLabels, _depthFromRay, _labelVotes;

        /// <summary>
        /// TEST 402: every map nameplate's anchor, as WorldToScreen projects it -
        /// on TWParallel workers, hence the lock. Each one weighs in on the plane
        /// depth by how near the middle of the view it is.
        ///
        /// Why the labels and not the terrain: a widget on the panel is exact for
        /// the eye position it was laid out from, and stays up two frames plus the
        /// pipeline's one; turning the head carries the eyes a few millimetres in
        /// that time, which moves the widget by that x (1/its depth - 1/panel
        /// depth). Still, it is exact - "standing still fixes it"; moving, the two
        /// eyes' images (a frame apart in age) move by different amounts - "a
        /// double that slides". The head-forward ray (TEST 398) mostly sailed past
        /// the table to the horizon and left the panel at 2.47 m with the labels
        /// 0.4-1 m away. The labels' own depth makes that factor ~0 where you look.
        /// </summary>
        internal static void NoteLabel(Vec3 world)
        {
            if (!Live || VrCameraDriver.MapScale <= 0f)
                return;

            MatrixFrame head = VrCameraDriver.HeadFrame;
            Vec3 v = world - head.origin;
            float len = v.Length;
            float z = Vec3.DotProduct(v, head.rotation.f);   // depth along the panel's normal
            if (len < 1e-4f || z <= 0f)
                return;

            // TEST 403: only what can be on the table. Vanilla projects EVERY
            // settlement every frame, visible or not, and the hundreds beyond the
            // table outvoted the few on it - the plane sat at the 2.47 m clamp for
            // the whole TEST 402 run while "set by the nameplates" on every frame.
            float metres = z / VrCameraDriver.MapScale;
            if (metres > VrUiSettings.Distance || metres < 0.5f * UiNearM)
                return;

            // Squared, so the label nearest the middle of the view dominates
            // rather than averaging two depths into a plane that suits neither.
            float c = z / len - LabelConeCos;
            if (c <= 0f)
                return;
            float w = c * c;

            lock (LabelLock)
            {
                _labelDepthSum += w * z;
                _labelWeightSum += w;
                _labelVotes++;
            }
        }

        /// <summary>TEST 398/402: where the interface plane goes this frame.</summary>
        // TEST 632 probe: "the town names tilt and shake for a moment on the first
        // walk after a load, the head still". Any frame the table (the game camera's
        // target) or the names' depth jumps, with what moved; 40 lines per 10 s.
        private static Vec3 _probeAt;
        private static float _probeYaw, _probeDepth, _probeDist;
        private static bool _probePrimed;
        private static int _probeLines;
        private static long _probeWindow;

        private static void MotionProbe(MatrixFrame table, float distance)
        {
            float yaw = (float)Math.Atan2(table.rotation.f.y, table.rotation.f.x);
            if (_recentre || !_probePrimed)
            {
                _probePrimed = true;
                _probeAt = table.origin;
                _probeYaw = yaw;
                _probeDepth = UiDepthMetres;
                _probeDist = distance;
                return;
            }

            Vec3 d = table.origin - _probeAt;
            float mm = d.Length / TableScale * 1000f;
            float zMm = d.z / TableScale * 1000f;
            float dYaw = (yaw - _probeYaw) * 57.2958f;
            if (dYaw > 180f) dYaw -= 360f;
            if (dYaw < -180f) dYaw += 360f;
            float dDepth = UiDepthMetres - _probeDepth;
            float dDist = distance - _probeDist;
            _probeAt = table.origin;
            _probeYaw = yaw;
            _probeDepth = UiDepthMetres;
            _probeDist = distance;

            if (mm < 3f && Math.Abs(dYaw) < 0.2f && Math.Abs(dDepth) < 0.05f)
                return;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _probeWindow > 10 * System.Diagnostics.Stopwatch.Frequency)
            {
                _probeWindow = now;
                _probeLines = 0;
            }
            if (++_probeLines > 40)
                return;

            var party = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty;
            VrLog.Info(FormattableString.Invariant(
                $"Map motion (TEST 632): table moved {mm:F1} mm ({zMm:F1} up), turned {dYaw:F2} deg, names' depth {dDepth:+0.00;-0.00} m to {UiDepthMetres:F2}, zoom {dDist:+0.0;-0.0}; party {(party != null && party.IsMoving ? "moving" : "still")}, time {Campaign.Current?.TimeControlMode}."));
        }

        private static void UpdateUiDepth(SceneView view)
        {
            float far = VrUiSettings.Distance;
            float scale = VrCameraDriver.MapScale;
            float want = far;

            double sum, weight;
            lock (LabelLock)
            {
                sum = _labelDepthSum;
                weight = _labelWeightSum;
                _labelDepthSum = _labelWeightSum = 0.0;
            }

            if (weight > 0.0 && scale > 0f)
            {
                want = Math.Max(UiNearM, Math.Min(far, (float)(sum / weight) / scale));
                _depthFromLabels++;
            }
            else
            {
                // No nameplate near the middle of the view: the terrain ahead.
                MatrixFrame head = VrCameraDriver.HeadFrame;
                if (view.RayCastForClosestEntityOrTerrain(head.origin, head.origin + head.rotation.f * (far * scale),
                                                          out float hit, out Vec3 _, 0.01f))
                    want = Math.Max(UiNearM, Math.Min(far, hit / scale));
                _depthFromRay++;
            }

            // ponytail: a per-frame EMA (~0.1 s at 60 fps), so it follows the frame
            // rate; scale by dt if that ever shows.
            UiDepthMetres = UiDepthMetres <= 0f ? want : UiDepthMetres + (want - UiDepthMetres) * 0.15f;
        }

        private static bool EnsureBound(MapScreen screen, SceneView view)
        {
            Texture target = VrStereoRenderer.ColorTarget(0);
            if (target == null || !VrStereoRenderer.TexturesReady)
                return false;

            if (!ReferenceEquals(view, _boundView) || !ReferenceEquals(target, _boundTarget))
            {
                // Not before the game says the scene can render: the map view is
                // created disabled and enabled a few frames after activation.
                if (!view.ReadyToRender())
                    return false;

                view.SetAutoDepthTargetCreation(true);
                view.SetRenderTarget(target);

                _boundScreen = screen;
                _boundView = view;
                _boundTarget = target;
                _appliedScale = -1f;

                VrLog.Info($"Map stereo: bound map scene view to the eye texture "
                           + $"(tabletop, map_table_scale {TableScale:F0} units per metre).");
            }

            float factor = VrResolution.LiveFactor;
            if (Math.Abs(factor - _appliedScale) >= 1e-3f)
            {
                view.SetScale(new Vec2(factor, factor));
                _appliedScale = factor;
            }

            return true;
        }

        /// <summary>
        /// The right hand's aim ray, in place of the mouse ray, for every map click,
        /// hover and ground cursor. Far end at <paramref name="length"/> map units.
        /// </summary>
        public static bool TryLaser(SceneView view, float length, out Vec3 near, out Vec3 far)
        {
            near = far = Vec3.Zero;

            if (!Live || !Laser || (view != null && !ReferenceEquals(view, _boundView)))
                return false;

            if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok
                || input.Right.IsActive == 0)
                return false;

            // ALWAYS, since TEST 283 (the user wants the beam on all the time).
            // Safe now that zoom-to-cursor is off (TEST 279) - that, not the ray,
            // is what flew the camera. A is still the click.
            MatrixFrame hand = VrCameraDriver.MapStageToWorld(
                VrMath.ToBannerlord(input.Right.AimOrientation, input.Right.AimPosition));
            near = hand.origin;
            far = hand.origin + MapBeamDir(hand) * length;   // TEST 651: the smoothed beam
            _lasers++;
            return true;
        }

        // --- the red beam --------------------------------------------------------
        //
        // A real entity in the map scene, so the engine draws it in both eyes at
        // its true depth. A unit box along +Y, stretched per frame by its frame's
        // rotation columns: f to the hit distance, s and u to the beam's width.

        /// <summary>Beam width in room millimetres.</summary>
        private static readonly float BeamMm = Clamp(VrConfig.Float("map_laser_width_mm", 3f), 0.5f, 30f);

        private static GameEntity _beam;
        private static UIntPtr _beamScene;
        private static bool _beamFailed;

        private static void UpdateBeam(SceneView view)
        {
            if (_beamFailed)
                return;

            try
            {
                // TEST 378: on a map widget the native beam goes to the widget
                // instead, so this one would point through the table at nothing.
                if (!Live || !Laser || VrLaser.OnMapWidget)
                {
                    HideBeam();
                    return;
                }

                Scene scene = view.GetScene();
                if (scene == null)
                    return;

                if (_beam == null || _beamScene != scene.Pointer)
                {
                    _beam = CreateBeam(scene);
                    _beamScene = scene.Pointer;
                }

                // Always on (TEST 283). A is the game's own map click, so pressing
                // it travels to where the beam points.
                if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok
                    || input.Right.IsActive == 0)
                {
                    HideBeam();
                    return;
                }

                MatrixFrame hand = VrCameraDriver.MapStageToWorld(
                    VrMath.ToBannerlord(input.Right.AimOrientation, input.Right.AimPosition));
                Vec3 dir = MapBeamDir(hand);   // TEST 651: the smoothed beam
                float scale = VrCameraDriver.MapScale;

                // Stop at the terrain: 3 m of room is more than any table.
                float length = 3f * scale;
                if (view.RayCastForClosestEntityOrTerrain(hand.origin, hand.origin + dir * length,
                                                          out float hit, out Vec3 _, 0.01f))
                    length = hit;

                float width = BeamMm * 0.001f * scale;

                MatrixFrame frame = hand;
                frame.rotation.s = hand.rotation.s * width;
                frame.rotation.f = dir * Math.Max(length, 1e-3f);
                frame.rotation.u = hand.rotation.u * width;

                _beam.SetGlobalFrame(in frame, true);
                _beam.SetVisibilityExcludeParents(true);
            }
            catch (Exception ex)
            {
                _beamFailed = true;
                VrLog.Error("Map laser: could not draw the beam; clicking still follows the "
                            + "hand.", ex);
            }
        }

        /// <summary>TEST 651: the right-hand beam's smoothed direction on the table.</summary>
        private static Vec3 MapBeamDir(MatrixFrame hand) =>
            VrLaser.DrawnDir(1, hand.rotation.f,
                             p => VrCameraDriver.MapStageToWorld(VrMath.ToBannerlord(VrLaser.Level, p)).origin);

        private static void HideBeam()
        {
            try
            {
                _beam?.SetVisibilityExcludeParents(false);
            }
            catch
            {
                // The map scene is gone with its screen; the next map makes a new beam.
                _beam = null;
            }
        }

        private static GameEntity CreateBeam(Scene scene)
        {
            Material material = Material.GetDefaultMaterial().CreateCopy();
            material.SetTexture(Material.MBTextureType.DiffuseMap, Texture.GetFromResource("plain_red"));

            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(mesh);

            // A 1 x 1 x 1 box from y = 0 to 1, centred on x and z; both windings on
            // every side so it shows whichever way the material culls.
            Vec3 red = new Vec3(1f, 0f, 0f);
            Vec3[] c =
            {
                new Vec3(-0.5f, 0f, -0.5f), new Vec3(0.5f, 0f, -0.5f),
                new Vec3(0.5f, 0f, 0.5f),   new Vec3(-0.5f, 0f, 0.5f),
                new Vec3(-0.5f, 1f, -0.5f), new Vec3(0.5f, 1f, -0.5f),
                new Vec3(0.5f, 1f, 0.5f),   new Vec3(-0.5f, 1f, 0.5f),
            };
            int[] quads = { 0, 1, 5, 4,  1, 2, 6, 5,  2, 3, 7, 6,  3, 0, 4, 7,  0, 3, 2, 1,  4, 5, 6, 7 };
            Vec2 uv = new Vec2(0.5f, 0.5f);

            for (int q = 0; q < quads.Length; q += 4)
            {
                Vec3 a = c[quads[q]], b = c[quads[q + 1]], d = c[quads[q + 2]], e = c[quads[q + 3]];
                ops.AddTriangle(a, b, d, uv, uv, uv, red);
                ops.AddTriangle(a, d, e, uv, uv, uv, red);
                ops.AddTriangle(a, d, b, uv, uv, uv, red);
                ops.AddTriangle(a, e, d, uv, uv, uv, red);
            }

            ops.ComputeCornerNormals();
            ops.FinalizeEditing();
            mesh.RecomputeBoundingBox();
            mesh.Color = 0xFFFF2020;

            GameEntity entity = GameEntity.CreateEmptyDynamic(scene, false);
            entity.AddMesh(mesh, true);
            entity.SetVisibilityExcludeParents(false);

            VrLog.Info($"Map laser: red beam created ({BeamMm:F1} mm wide).");
            return entity;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return float.IsNaN(v) ? lo : Math.Max(lo, Math.Min(hi, v));
        }
    }
}
