using System;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Makes the head move the game's own look the way the mouse does.
    ///
    /// WHAT THIS IS NOW, AND WHAT IT USED TO BE
    /// ----------------------------------------
    /// It used to WRITE the engine's camera - CameraBearing and the elevation
    /// backing field - through a closed loop that watched where the camera ended
    /// up and pushed it toward the head. That is what produced "engine yaw 754.1"
    /// while the headset moved 158 degrees, and it needed a calibration phase, a
    /// rate limit, a wrong-way counter and a gain guard to stay survivable.
    ///
    /// It does not write the camera at all any more. It adds the head's movement
    /// to _cameraBearingDelta and _cameraElevationDelta - the exact fields the
    /// MOUSE writes, read by the engine on the same frame to build the camera.
    /// The request was "head tracking that simulates the mouse movement", and
    /// this is that literally: the head becomes another producer of look input,
    /// alongside the mouse, merged by the engine's own code.
    ///
    /// Nothing here can run away. There is no target and no error term, so there
    /// is nothing to diverge from; the engine consumes the delta and zeroes it,
    /// exactly as it does the mouse's.
    ///
    /// THE ONE HAZARD, AND HOW IT IS CANCELLED
    /// ---------------------------------------
    /// The rendered view is built as body-heading composed with the head pose. If
    /// head yaw is pushed into the game's look AND the body heading is then read
    /// from something that followed it, the head's rotation is applied twice and
    /// the world turns twice as far as the head does.
    ///
    /// It used to be cancelled by subtraction - BodyYaw = lookYaw - headYaw -
    /// which is algebraically perfect and operationally fatal. It makes the
    /// rendered view yaw come out as exactly lookYaw, so the head can only turn
    /// the VIEW to the extent that it turns the GAME CAMERA. The moment the
    /// injection stops landing, head tracking stops with it, and the player is
    /// left locked to the mouse. That happened; it is what this file is now
    /// written the other way round to prevent.
    ///
    /// BodyYaw is INTEGRATED FROM THE PLAYER MOUSE INSTEAD, and never reads the
    /// game look at all:
    ///
    ///     BodyYaw += yawSign * mouseDelta
    ///
    /// so the view is BodyYaw + head and the head always turns it, injection or
    /// no injection. Head tracking stops being negotiable and aim following the
    /// head becomes best-effort. An integrator can drift where an observation
    /// cannot, so the drift is measured and reported: _aimDivergence is the gap
    /// between where the game aims and where the view points, and it is the one
    /// number that says whether the crosshair means anything.
    ///
    /// THE SIGN IS MEASURED, NEVER ASSUMED
    /// -----------------------------------
    /// Which way a positive bearing delta turns the world is a convention nobody
    /// wrote down. It is learned from the player's own mouse, before anything is
    /// injected: correlate the change in the agent's world look direction against
    /// the delta that caused it. Until the sign is known nothing is written and
    /// the anchor keeps its normal behaviour, so calibration is invisible.
    ///
    /// The delay between writing a delta and seeing the look move matters. The
    /// delta read in this prefix has not been applied yet - the change visible
    /// this frame came from the PREVIOUS frame's delta - so the correlation is
    /// against the previous frame's value. Comparing same-frame values, which is
    /// the obvious thing to write, correlates a cause with an effect that has not
    /// happened yet and scores noise.
    /// </summary>
    public static class VrEngineAim
    {
        public static bool Enabled { get; } = VrConfig.Bool("engine_aim", true);

        // YAW WAS OFF BY DEFAULT, TWICE, FOR THE SAME REASON - AND IT IS FIXED NOW.
        //
        // Under the old subtraction anchor, feeding head yaw into the game look
        // and keeping free head-look were mutually exclusive, and the log said so
        // plainly both times:
        //
        //   Engine aim: head yaw 33.7 deg, game look yaw -30.5, body -64.2
        //
        // -64.2 + 33.7 = -30.5. The view was pinned to the game look yaw and the
        // head had no independent effect at all - reported the second time as
        // "locked in the direction of the mouse camera", which is exactly right.
        //
        // BodyYaw is integrated from the mouse now and never reads the game look,
        // so the two are no longer in tension: the head turns the view whatever
        // the injection does. See the class note.
        private static readonly bool DriveYaw = VrConfig.Bool("head_drive_yaw", true);

        // Pitch has no such problem and stays on. The anchor is yaw-only by
        // construction, so head pitch is never taken back out of the view and the
        // injection cannot feed back into what the player sees.
        private static readonly bool DrivePitch = VrConfig.Bool("head_drive_pitch", true);

        /// <summary>Most the head may contribute to the game's look in one frame,
        /// radians. At 72 Hz a fast head turn is well under this; it exists so a
        /// tracking glitch or a recentre cannot fling the camera.</summary>
        private static readonly float MaxStep = VrConfig.Float("head_drive_max_step", 0.08f);

        /// <summary>
        /// How much of the remaining aim error to correct each frame.
        ///
        /// Not 1.0, and the delay is why: what this reads is the error as it was
        /// when the engine last rebuilt the camera, so a full correction is
        /// acting on information one frame old and overshoots into a ring. A
        /// third per frame is heavily damped and still fast - at 120 fps a sixty
        /// degree error is under a degree inside a tenth of a second, which
        /// reads as instant.
        ///
        /// Raise it if the aim feels like it lags a fast head turn; lower it if
        /// the crosshair visibly hunts around the target.
        /// </summary>
        private static readonly float Gain = VrConfig.Float("head_aim_gain", 0.35f);

        /// <summary>
        /// Write the aim outright instead of nudging it with deltas. Default on:
        /// deltas cannot get past the first-person look constraint, see _bearing.
        /// 0 restores the delta injection, which is what every earlier build did.
        /// </summary>
        private static readonly bool AbsoluteAim = VrConfig.Bool("head_aim_absolute", true);

        /// <summary>
        /// Whether the mouse still turns the view.
        ///
        /// Off by default now that the head writes the bearing outright: two
        /// authors for one value fight, and the head is the one the player is
        /// actually pointing. The mouse keeps every other function it had - menus,
        /// orders, the campaign map - this is only its contribution to where the
        /// character is looking.
        /// </summary>
        private static readonly bool MouseYaw = VrConfig.Bool("mouse_yaw", false);
        private static readonly bool MousePitch = VrConfig.Bool("mouse_pitch", false);

        /// <summary>
        /// Start driving on the first frame instead of waiting for calibration, when
        /// the aim is written outright (TEST 180).
        ///
        /// Calibration waits for the player's own look input - a mouse, a stick, a
        /// turning horse - before the head reaches CameraBearing. Until then the
        /// game builds its camera from the body heading while the head looks
        /// anywhere, and the engine draws nothing outside where the BODY faces:
        /// "sky only behind me until I move once". One turn calibrated it, and it
        /// stayed fixed for the battle.
        ///
        /// In absolute mode the only thing calibration learns is the sign that
        /// integrates the mouse into BodyYaw, and that sign is not a matter of
        /// taste. From the game itself: Vec3.RotationZ = atan2(-x, y), the negative
        /// of this file's yaw, and UpdateCamera does CameraBearing += delta - so yaw
        /// moves by -delta. RotationX = atan2(z, horizontal) and CameraElevation +=
        /// delta, so pitch moves by +delta. Every calibration in the logs measured
        /// exactly -1 / +1. 0 restores the wait.
        /// </summary>
        private static readonly bool SkipCalibration =
            VrConfig.Bool("engine_aim_skip_calibration", true);

        /// <summary>
        /// Multiplies the engine's vertical field of view, from the CameraViewAngle
        /// getter postfix.
        ///
        /// This was a workaround for an architecture that no longer runs: with the
        /// engine's camera given the headset's own frustum directly, there is
        /// nothing left for a scale factor to correct. Left in place because the
        /// patch that reads it is gated on it, and at 1.0 the patch is skipped
        /// entirely.
        /// </summary>
        public static float ViewAngleScale { get; } =
            VrConfig.Float("engine_view_angle_scale", 1.0f);

        public static bool WidensViewAngle => Enabled && ViewAngleScale > 1.001f;

        private static bool _loggedViewAngle;

        /// <summary>Called from the CameraViewAngle getter postfix.</summary>
        public static float ScaleViewAngle(float engineValue)
        {
            if (!_loggedViewAngle)
            {
                _loggedViewAngle = true;
                VrLog.Info(string.Format(
                    "Engine aim: CameraViewAngle reads {0:F1} deg vertical; returning {1:F1}.",
                    engineValue, engineValue * ViewAngleScale));
            }

            return engineValue * ViewAngleScale;
        }

        // --- reflected access -------------------------------------------------
        //
        // Both delta fields are non-public, so they go through their names. All of
        // it is resolved once, and any failure disables the feature rather than
        // throwing at frame rate.
        private static AccessTools.FieldRef<MissionScreen, float> _bearingDelta;
        private static AccessTools.FieldRef<MissionScreen, float> _elevationDelta;

        /// <summary>
        /// The bearing and elevation themselves, not the deltas.
        ///
        /// WHY THE ABSOLUTE VALUE AND NOT THE DELTA, AFTER THREE ROUNDS OF DELTAS
        ///
        /// Because in first person the engine will not accept a delta. From
        /// MissionScreen.UpdateCamera, when CameraIsFirstPerson is set:
        ///
        ///     CalculateNewBearingAndElevationForFirstPerson(...out newBearing...)
        ///     if (newBearing != num15)
        ///         _cameraBearingDelta = ClampFloat(WrapAngle(newBearing - CameraBearing),
        ///                                          MinMax(-num17, num17));
        ///
        /// where num17 is our own delta. The clamp range is symmetric about zero
        /// and bounded BY THE VALUE WE WROTE, so the engine can only ever shrink
        /// an injected delta, never grow it - and what it shrinks it to is the
        /// first-person look constraint, the limit on how far a head may turn
        /// relative to its body. Ask for more and the aim parks at the boundary,
        /// which is exactly what the log measured: aim-vs-view sitting at about
        /// sixty degrees and staying there.
        ///
        /// No amount of tuning the injected delta gets past that, because the
        /// engine is not accumulating our deltas - it is recomputing the bearing
        /// from its own constraint every frame and using our delta only as a
        /// permission slip for how far it may move.
        ///
        /// Writing the bearing itself sidesteps the whole mechanism. With the
        /// delta left at zero the clamp range collapses to [0, 0], so the engine
        /// adds nothing to what we wrote and the value we set is the value it
        /// builds the camera from.
        ///
        /// They are auto-properties - CameraBearing's setter is public and
        /// CameraElevation's is not - so both go through their backing fields.
        /// One mechanism for both beats two, and a field reference costs a load
        /// where a reflected property setter costs an invoke at frame rate.
        /// </summary>
        private static AccessTools.FieldRef<MissionScreen, float> _bearing;
        private static AccessTools.FieldRef<MissionScreen, float> _elevation;

        private static bool _resolved;
        private static bool _usable;

        private enum Phase { Calibrating, Driving, Disabled }

        private static Phase _phase = Phase.Calibrating;

        /// <summary>Body heading in world radians, 0 = facing +Y. The game's look
        /// yaw with the head's own contribution taken back out - see the class
        /// note.</summary>
        public static float BodyYaw { get; private set; }

        /// <summary>True once the head is genuinely contributing to the game's
        /// look, which is the only condition under which the anchor must stop
        /// reading the agent's look direction directly.</summary>
        public static bool DrivingYaw => Enabled && DriveYaw && _phase == Phase.Driving;

        /// <summary>
        /// Whether the VR anchor must stop reading the agent's look direction.
        ///
        /// True when ANYTHING is steering that look, not only this class.
        /// VrHeadAim writes Agent.LookDirection outright, so an anchor built from
        /// it would apply the head twice just as surely as the delta injection
        /// would. One condition, both mechanisms - a second answer to the same
        /// question is how two of them end up disagreeing.
        /// </summary>
        public static bool AnchorMustIgnoreLook =>
            DrivingYaw || VrHeadAim.Active || (AbsoluteAim && _phase == Phase.Driving);

        /// <summary>
        /// Whether BodyYaw is currently being integrated, and so whether
        /// BodyHeading() is a number anyone may rely on.
        ///
        /// VrHeadAim checks this before it drives anything. Driving the agent's
        /// look without a body heading to fall back to would leave the anchor
        /// reading a look direction it is itself writing, which is the doubling
        /// this whole arrangement exists to prevent.
        /// </summary>
        public static bool MaintainsBodyHeading => Enabled && _usable && _phase == Phase.Driving;

        /// <summary>The body basis for the VR anchor: level, and carrying only the
        /// part of the game's look that did not come from the head.</summary>
        public static Mat3 BodyHeading()
        {
            Mat3 m = default;
            m.f = new Vec3((float)Math.Sin(BodyYaw), (float)Math.Cos(BodyYaw), 0f, -1f);
            m.u = new Vec3(0f, 0f, 1f, -1f);
            m.s = Vec3.CrossProduct(m.f, m.u);
            m.s.Normalize();
            return m;
        }

        // ---------------------------------------------------------------------

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;

            try
            {
                _bearingDelta = AccessTools.FieldRefAccess<MissionScreen, float>("_cameraBearingDelta");
                _elevationDelta = AccessTools.FieldRefAccess<MissionScreen, float>("_cameraElevationDelta");

                _bearing = AccessTools.FieldRefAccess<MissionScreen, float>(
                    "<CameraBearing>k__BackingField");
                _elevation = AccessTools.FieldRefAccess<MissionScreen, float>(
                    "<CameraElevation>k__BackingField");

                _usable = true;
            }
            catch (Exception ex)
            {
                _usable = false;
                _phase = Phase.Disabled;
                VrLog.Error("Engine aim: MissionScreen's look-delta fields are not where "
                            + "they were; the head will not drive the game's look.", ex);
            }
        }

        // Observed state, all of it read rather than integrated.
        private static bool _seeded;
        private static float _lastLookYaw;
        private static float _lastLookPitch;
        private static float _prevMouseYaw;

        /// <summary>Game aim yaw minus rendered view yaw. Zero while the head is
        /// genuinely steering the game; growing means the crosshair is lying.</summary>
        private static float _aimDivergence;
        private static float _prevMousePitch;

        private static bool _haveHeadBase;
        private static float _lastHeadYaw;
        private static float _lastHeadPitch;

        private static int _yawSign;
        private static int _pitchSign;
        private static int _yawScore;
        private static int _pitchScore;
        private static int _calibFrames;
        private static bool _warnedNoCalibration;

        private static int _logTick;

        private static int _absTick;
        private static bool _loggedAbsolute;

        /// <summary>
        /// Reports how far the game's aim is from the view now that it is being
        /// written rather than nudged. This is the number that was parked at
        /// about sixty degrees; it should sit near zero.
        /// </summary>
        private static void LogAbsolute(float headYaw, float lookYaw)
        {
            if (!_loggedAbsolute)
            {
                _loggedAbsolute = true;
                VrLog.Info("Aim is now WRITTEN to the engine's own bearing and elevation "
                           + "rather than nudged with deltas, and the mouse no longer "
                           + "contributes to it. Deltas could not get past the "
                           + "first-person look constraint, which clamped every injection "
                           + "to the neck limit and parked the aim about sixty degrees off "
                           + "the view. Set head_aim_absolute = 0 to go back, or "
                           + "mouse_yaw = 1 to give the mouse its turn back.");
            }

            if (++_absTick < 450)
                return;
            _absTick = 0;

            VrLog.Info(string.Format(
                "Aim written: head yaw {0:F1} deg, game look yaw {1:F1}, body {2:F1}; "
                + "aim-vs-view {3:F1} deg (this is the number that used to sit at -60; "
                + "near zero means the character really is facing where you look).",
                headYaw * 57.2957795f, lookYaw * 57.2957795f, BodyYaw * 57.2957795f,
                _aimDivergence * 57.2957795f));
        }

        /// <summary>
        /// Runs in a PREFIX of MissionScreen.UpdateCamera, which is where the mouse's
        /// own contribution already sits: the engine reads both delta fields
        /// immediately after this to build the frame's camera. A postfix would write
        /// into a value that has already been consumed and zeroed.
        /// </summary>
        public static void Apply(MissionScreen screen)
        {
            if (!Enabled || screen == null)
                return;

            Resolve();
            if (!_usable || _phase == Phase.Disabled)
                return;

            // Not "nothing to drive" any more: VrHeadAim needs the body heading
            // integrated here even when this class injects nothing itself, or the
            // anchor it falls back to would be a stale number.
            if (!DriveYaw && !DrivePitch && !VrHeadAim.Enabled)
                return;

            if (!VrCameraDriver.HaveHeadAngles)
                return;

            ReportCameraSurvival(screen);

            try
            {
                // The agent's look direction, NOT the camera's. In this mode we
                // overwrite the engine's camera ourselves every frame, so reading it
                // back would only tell us what we last wrote. The agent's look is
                // driven by the engine from the same bearing and elevation and we
                // never write to it, so it is the honest read-back.
                Agent agent = Mission.Current?.MainAgent;
                if (agent == null)
                    return;

                Vec3 look = agent.LookDirection;
                if (look.LengthSquared < 1e-6f)
                    return;

                float lookYaw = (float)Math.Atan2(look.x, look.y);
                float lookPitch = (float)Math.Asin(Clamp(look.z, -1f, 1f));

                float headYaw = VrCameraDriver.HeadYaw;
                float headPitch = VrCameraDriver.HeadPitch;

                // Read BEFORE anything is added, so the calibration stimulus is the
                // player's input alone and never our own contribution.
                float mouseYaw = _bearingDelta(screen);
                float mousePitch = _elevationDelta(screen);

                if (!_seeded)
                {
                    _seeded = true;
                    _lastLookYaw = lookYaw;
                    _lastLookPitch = lookPitch;
                    _prevMouseYaw = mouseYaw;
                    _prevMousePitch = mousePitch;
                    BodyYaw = lookYaw;

                    if (AbsoluteAim && SkipCalibration && _phase == Phase.Calibrating)
                    {
                        _yawSign = -1;
                        _pitchSign = 1;
                        _phase = Phase.Driving;
                        _haveHeadBase = false;
                        VrLog.Info("Engine aim: driving from the first frame with the game's own "
                                   + "conventions (bearing sign -1, elevation sign 1) instead of "
                                   + "waiting for look input to calibrate, so the game's camera "
                                   + "follows the head straight after spawning. Set "
                                   + "engine_aim_skip_calibration = 0 to wait again.");
                    }

                    return;
                }

                float lookYawMoved = WrapPi(lookYaw - _lastLookYaw);
                float lookPitchMoved = lookPitch - _lastLookPitch;
                _lastLookYaw = lookYaw;
                _lastLookPitch = lookPitch;

                if (_phase == Phase.Calibrating)
                {
                    Calibrate(lookYawMoved, lookPitchMoved);
                    _prevMouseYaw = mouseYaw;
                    _prevMousePitch = mousePitch;
                    return;
                }

                // THE BODY HEADING IS NOW INTEGRATED FROM THE MOUSE, NOT DERIVED
                // FROM THE GAME'S LOOK. This is the fix for "head aiming is
                // locked in the direction of the mouse camera".
                //
                // The subtraction that used to live here - BodyYaw = lookYaw -
                // headYaw - is algebraically perfect and operationally fatal. It
                // makes the rendered view yaw come out as exactly lookYaw, which
                // is elegant right up to the moment the injection does not land.
                // Then the game's look follows the mouse alone, the view follows
                // the game's look, and moving your head does NOTHING. Head
                // tracking becomes entirely conditional on a write into a private
                // engine field continuing to work.
                //
                // This file's own header wrote that down before I switched the
                // setting on:
                //
                //   "the head can only turn the VIEW to the extent that it turns
                //    the GAME'S CAMERA. If the injection is doing nothing, the
                //    head does nothing"
                //
                // Integrating the player's own mouse delta instead breaks that
                // dependency completely. The view is BodyYaw + head, and BodyYaw
                // never reads lookYaw, so the head ALWAYS turns the view whether
                // or not the aim injection works. Aim following the head becomes
                // best-effort; head tracking stops being negotiable.
                //
                // mouseYaw was read above, before our own contribution was added,
                // so it is the player's input and nothing else. The sign is the
                // one calibration already measured.
                BodyYaw = WrapPi(BodyYaw + _yawSign * mouseYaw);

                // How far the game's aim has drifted from where the view points.
                // Zero means the injection is landing and the crosshair marks the
                // real aim. Growing by roughly the head's own movement means the
                // injection is doing nothing and the crosshair is decorative -
                // which is worth knowing plainly rather than inferring from how
                // the game feels.
                _aimDivergence = WrapPi(lookYaw - WrapPi(BodyYaw + headYaw));

                CheckDirection(lookYawMoved, lookPitchMoved);

                if (!_haveHeadBase)
                {
                    _haveHeadBase = true;
                    _lastHeadYaw = headYaw;
                    _lastHeadPitch = headPitch;
                    _prevMouseYaw = mouseYaw;
                    _prevMousePitch = mousePitch;
                    return;
                }

                _lastHeadYaw = headYaw;
                _lastHeadPitch = headPitch;

                // THE AIM IS WRITTEN, NOT NUDGED. See the note on _bearing.
                //
                // The direction is the rendered view's own world forward, and the
                // conversion to bearing and elevation is the ENGINE'S - Vec3's
                // RotationZ and RotationX are what MissionScreen itself uses to
                // turn a look direction into these two numbers:
                //
                //     CameraBearing = mainAgent.LookDirection.RotationZ;
                //     CameraElevation = mainAgent.LookDirection.RotationX;
                //
                // so there is no convention to get wrong and no sign to
                // calibrate. That matters: every previous attempt here carried a
                // learned sign, and a learned sign is a thing that can be learned
                // backwards.
                if (AbsoluteAim)
                {
                    Vec3 want = VrCameraDriver.HeadFrame.rotation.f;

                    if (want.LengthSquared > 1e-6f)
                    {
                        want.Normalize();

                        // The mouse's own contribution, removed before the engine
                        // reads it. Leaving it would add a second author to a
                        // value we are now setting outright, and the two would
                        // fight at whatever rate the mouse was moved.
                        if (!MouseYaw)
                            _bearingDelta(screen) = 0f;

                        if (!MousePitch)
                            _elevationDelta(screen) = 0f;

                        if (DriveYaw)
                            _bearing(screen) = want.RotationZ;

                        if (DrivePitch)
                            _elevation(screen) = want.RotationX;

                        _prevMouseYaw = mouseYaw;
                        _prevMousePitch = mousePitch;

                        LogAbsolute(headYaw, lookYaw);
                        return;
                    }
                }

                // CLOSED LOOP, AND THAT IS THE WHOLE CHANGE.
                //
                // This used to inject the head's per-frame DELTA: the head moved
                // three degrees, so push three degrees into the game's look. It
                // is the obvious thing to write and it is open loop, which means
                // nothing anywhere corrects an error once one exists. And errors
                // always exist - MaxStep clamps a fast turn, a dropped frame
                // loses a delta, the engine clamps its own elevation near
                // vertical, and each of those quietly subtracts from the total.
                // The offset that leaves never comes back.
                //
                // The log said so plainly, and consistently:
                //
                //     aim-vs-view -60.2 ... -73.7 ... -58.3 ... -61.4 ... -61.8
                //
                // Not noise around zero - parked at about sixty degrees. The
                // character was aiming a sixth of a turn away from where the
                // player was looking, steadily, which is exactly what "the body
                // does not follow my head" is.
                //
                // So drive the ERROR to zero instead of echoing the input.
                // _aimDivergence is already that error - the game's aim minus the
                // rendered view - and it was computed for the log and never used
                // to steer anything. Feeding a fraction of it back each frame
                // makes the aim converge on the view and STAY there, and it
                // self-corrects after every clamp, hitch and dropped frame
                // rather than accumulating them.
                float errYaw = _aimDivergence;

                // The anchor is yaw-only by construction, so the rendered view's
                // pitch IS the head's pitch - no body term to add.
                float errPitch = lookPitch - headPitch;

                _injectedYaw = 0f;
                _injectedPitch = 0f;

                if (DriveYaw && Math.Abs(errYaw) > 1e-4f)
                {
                    // Negative because the error is measured as "how far the aim
                    // is PAST the view", and the correction has to come back.
                    _injectedYaw = _yawSign * Clamp(-errYaw * Gain, -MaxStep, MaxStep);
                    _bearingDelta(screen) += _injectedYaw;
                }

                if (DrivePitch && Math.Abs(errPitch) > 1e-4f)
                {
                    _injectedPitch = _pitchSign * Clamp(-errPitch * Gain, -MaxStep, MaxStep);
                    _elevationDelta(screen) += _injectedPitch;
                }

                _prevMouseYaw = mouseYaw;
                _prevMousePitch = mousePitch;

                if (++_logTick >= 450)
                {
                    _logTick = 0;
                    VrLog.Info(string.Format(
                        "Engine aim: head yaw {0:F1} deg, game look yaw {1:F1}, body {2:F1}; "
                        + "head pitch {3:F1}, game look pitch {4:F1}; aim-vs-view {5:F1} deg "
                        + "(near 0 = the head really is steering the game; growing = the "
                        + "injection is not landing and the crosshair is decorative).",
                        headYaw * 57.2957795f, lookYaw * 57.2957795f, BodyYaw * 57.2957795f,
                        headPitch * 57.2957795f, lookPitch * 57.2957795f,
                        _aimDivergence * 57.2957795f));
                }
            }
            catch (Exception ex)
            {
                _phase = Phase.Disabled;
                VrLog.Error("Engine aim failed; the head no longer drives the game's look. "
                            + "The rendered view is unaffected.", ex);
            }
        }

        // --- did the pose we wrote survive the frame? -------------------------
        //
        // The one measurement nothing in this mod has ever taken, and its absence
        // has cost several runs. Every instrument so far compares two of OUR OWN
        // numbers - the head pose in against the head pose out - so all of them
        // read perfectly while the engine rendered from a camera we never touched.
        // CameraPatch's header named that failure years of sessions ago and there
        // was still no line in the log that could detect it.
        //
        // THIS PREFIX IS THE ONLY PLACE IT CAN BE MEASURED. It runs before the
        // engine recomputes CombatCamera, so what the camera holds right now is
        // exactly what our UpdateCamera POSTFIX wrote at the end of the previous
        // frame, untouched since. Anywhere else in the frame reads the engine's own
        // value and proves nothing.
        //
        // Camera.Direction is the true view axis - the convention line at startup
        // measured it as -u of the frame - so this compares the yaw and pitch we
        // asked for against the yaw and pitch that were still there a frame later.
        //
        //   both differences near zero  -> the pose sticks; if the headset is still
        //                                  frozen the scene view is not rendering
        //                                  from CombatCamera at all
        //   differences large and alive -> something after our postfix is putting
        //                                  the camera back
        private static int _survivalTick;

        private static void ReportCameraSurvival(MissionScreen screen)
        {
            // 150, not 450. At 72 fps the old threshold needed six and a quarter
            // seconds of mission time, and the last run reached the main menu after
            // five and a half - so the one instrument built to answer the question
            // never printed once. An instrument that does not fit inside a real test
            // run is not an instrument.
            if (++_survivalTick < 150)
                return;
            _survivalTick = 0;

            try
            {
                TaleWorlds.Engine.Camera combat = screen.CombatCamera;
                if (combat == null || !VrCameraDriver.HaveEyeFrames)
                    return;

                Vec3 dir = combat.Direction;
                if (dir.LengthSquared < 1e-6f)
                    return;

                MatrixFrame wanted = VrCameraDriver.EyeFrame(VrAfrRenderer.CurrentEye);
                Vec3 want = wanted.rotation.f;

                float haveYaw = (float)Math.Atan2(dir.x, dir.y) * 57.2957795f;
                float havePitch = (float)Math.Asin(Clamp(dir.z, -1f, 1f)) * 57.2957795f;
                float wantYaw = (float)Math.Atan2(want.x, want.y) * 57.2957795f;
                float wantPitch = (float)Math.Asin(Clamp(want.z, -1f, 1f)) * 57.2957795f;

                VrLog.Info(string.Format(
                    "Engine camera SURVIVAL: we wrote yaw {0:F1} pitch {1:F1}, a frame later "
                    + "it reads yaw {2:F1} pitch {3:F1} (off by {4:F1} / {5:F1}). Near zero "
                    + "means the pose sticks and any frozen image is the scene view rendering "
                    + "from something else.",
                    wantYaw, wantPitch, haveYaw, havePitch,
                    WrapPi((wantYaw - haveYaw) / 57.2957795f) * 57.2957795f,
                    wantPitch - havePitch));
            }
            catch (Exception ex)
            {
                _survivalTick = int.MinValue / 2;
                VrLog.Error("Camera survival probe failed; disabling it.", ex);
            }
        }

        // --- calibration ------------------------------------------------------
        //
        // The player's mouse is the stimulus, and nothing is injected until the
        // answer is in - so this phase is invisible. There is no probe: a probe
        // would move the camera on its own before we know which way it moves it,
        // which is the one thing worth avoiding here.

        private static void Calibrate(float lookYawMoved, float lookPitchMoved)
        {
            _calibFrames++;

            // Against the PREVIOUS frame's delta: what moved the look this frame was
            // written last frame. See the class note.
            if (Math.Abs(_prevMouseYaw) > 0.0015f && Math.Abs(lookYawMoved) > 0.0005f)
                _yawScore += (Math.Sign(lookYawMoved) == Math.Sign(_prevMouseYaw)) ? 1 : -1;

            if (Math.Abs(_prevMousePitch) > 0.0015f && Math.Abs(lookPitchMoved) > 0.0005f)
                _pitchScore += (Math.Sign(lookPitchMoved) == Math.Sign(_prevMousePitch)) ? 1 : -1;

            if (Math.Abs(_yawScore) < 20)
            {
                // Say so once, or a player who never touches the mouse is left
                // wondering why the feature never switched on.
                if (_calibFrames == 900 && !_warnedNoCalibration)
                {
                    _warnedNoCalibration = true;
                    VrLog.Info("Engine aim: still learning which way the game's look "
                               + "input turns. Move the mouse briefly and it will "
                               + "settle; nothing is being written until it does.");
                }
                return;
            }

            _yawSign = Math.Sign(_yawScore);

            // Pitch borrows the yaw answer if it never got its own stimulus. Being
            // wrong about pitch cannot turn the world - the anchor is yaw-only, so
            // pitch never feeds back - and CheckDirection corrects it from what the
            // look actually does.
            _pitchSign = (_pitchScore != 0) ? Math.Sign(_pitchScore) : 1;

            _phase = Phase.Driving;
            _haveHeadBase = false;

            VrLog.Info(string.Format(
                "Engine aim calibrated after {0} frames: bearing sign {1}, elevation sign {2} "
                + "(yaw score {3}, pitch score {4}, pitch {5}). The head now feeds the game's "
                + "own look input: {6}.",
                _calibFrames, _yawSign, _pitchSign, _yawScore, _pitchScore,
                _pitchScore != 0 ? "measured" : "borrowed from yaw",
                (DriveYaw ? "yaw " : "") + (DrivePitch ? "pitch" : "")));
        }

        // --- did it go the way we asked? --------------------------------------
        //
        // Calibration can be fooled - by a mouse delta that arrived in the same
        // frame as a scripted camera move, by a single noisy window. Once driving,
        // the injection is its own stimulus and a much better one, so keep checking.
        // Only frames where the head was the DOMINANT cause count, or the player's
        // own mouse would be scored as our error.
        private static float _injectedYaw;
        private static float _injectedPitch;
        private static int _yawWrongWay;
        private static int _pitchWrongWay;

        private static void CheckDirection(float lookYawMoved, float lookPitchMoved)
        {
            if (Math.Abs(_injectedYaw) > 1e-4f &&
                Math.Abs(_injectedYaw) > Math.Abs(_prevMouseYaw) * 2f &&
                Math.Abs(lookYawMoved) > 1e-4f)
            {
                if (Math.Sign(lookYawMoved) != Math.Sign(_injectedYaw))
                {
                    if (++_yawWrongWay >= 30)
                    {
                        _yawSign = -_yawSign;
                        _yawWrongWay = 0;
                        VrLog.Info("Engine aim: bearing sign was backwards; flipped to "
                                   + _yawSign + ".");
                    }
                }
                else
                {
                    _yawWrongWay = 0;
                }
            }

            if (Math.Abs(_injectedPitch) > 1e-4f &&
                Math.Abs(_injectedPitch) > Math.Abs(_prevMousePitch) * 2f &&
                Math.Abs(lookPitchMoved) > 1e-4f)
            {
                if (Math.Sign(lookPitchMoved) != Math.Sign(_injectedPitch))
                {
                    if (++_pitchWrongWay >= 30)
                    {
                        _pitchSign = -_pitchSign;
                        _pitchWrongWay = 0;
                        VrLog.Info("Engine aim: elevation sign was backwards; flipped to "
                                   + _pitchSign + ".");
                    }
                }
                else
                {
                    _pitchWrongWay = 0;
                }
            }
        }

        /// <summary>
        /// The safety net. VrCameraDriver measures how far the rendered view turns
        /// per degree the headset turns; 1.0 is correct. The cancellation above
        /// makes doubling impossible in principle, so a sustained reading well
        /// above 1.0 means the premise is wrong somewhere and the honest response
        /// is to stop rather than to compensate.
        /// </summary>
        public static void ReportGain(float viewOverHead)
        {
            if (!DrivingYaw || viewOverHead < 1.4f)
            {
                _gainStrikes = 0;
                return;
            }

            if (++_gainStrikes < 2)
                return;

            _phase = Phase.Disabled;
            VrLog.Warn(string.Format(
                "Engine aim: the view turned {0:F2}x as far as the head over two windows "
                + "running. The head's contribution is not cancelling out of the body "
                + "heading as it should, so the head is no longer feeding the game's look.",
                viewOverHead));
        }

        private static int _gainStrikes;

        // --- helpers ----------------------------------------------------------

        private static float WrapPi(float a)
        {
            const float TwoPi = (float)(Math.PI * 2.0);
            while (a > Math.PI) a -= TwoPi;
            while (a < -Math.PI) a += TwoPi;
            return a;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        public static void Reset()
        {
            _phase = _phase == Phase.Disabled ? Phase.Disabled : Phase.Calibrating;
            _seeded = false;
            _haveHeadBase = false;
            _yawScore = 0;
            _pitchScore = 0;
            _calibFrames = 0;
            _logTick = 0;
            _yawWrongWay = 0;
            _pitchWrongWay = 0;
            _injectedYaw = 0f;
            _injectedPitch = 0f;
            _warnedNoCalibration = false;
        }
    }
}
