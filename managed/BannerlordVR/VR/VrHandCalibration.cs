using System;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Where each hand sits relative to the controller driving it.
    ///
    /// WHAT WENT WRONG WITH THE FIRST VERSION, AND HOW THE LOG SAYS SO
    ///
    /// It captured the whole rigid transform between the controller and the hand
    /// the animation had drawn:
    ///
    ///     grip = controllerWorld' * animatedHandWorld
    ///
    /// That is correct as arithmetic and wrong as a definition, and the log is the
    /// proof. Fourteen presses of the align key, fourteen answers:
    ///
    ///     offset (-0.029, -0.061,  0.105) m
    ///     offset (-0.101, -0.087,  0.004) m
    ///     offset (-0.633,  0.040,  0.425) m
    ///     offset ( 0.383, -0.370, -0.082) m
    ///
    /// A calibration that lands 63 cm from the hand on one press and 3 cm on the
    /// next is not measuring a property of the equipment. It is measuring where
    /// the animation happened to have the character's arm at the instant the key
    /// went down, which has nothing to do with where the player's hand is.
    ///
    /// POSITION AND ORIENTATION ARE NOT THE SAME QUESTION
    ///
    /// Split the transform and one half turns out not to need capturing at all.
    ///
    /// POSITION is already known. OpenXR defines the grip pose's position as the
    /// palm centroid of a closed fist, and the hand bone sits within a couple of
    /// centimetres of it. So the hand goes WHERE THE CONTROLLER IS - the offset is
    /// nought, give or take a wrist, and the give-or-take is a small tunable
    /// number rather than something measured off a running animation.
    ///
    /// ORIENTATION genuinely has to be captured, because it depends on how this
    /// skeleton's artist oriented the hand bone against how OpenXR orients a grip
    /// pose, and neither of those is written down anywhere we can read. The
    /// animation knows it: the weapon mesh is bound to the hand, so whatever
    /// rotation the animation gives that bone is a correct sword-holding
    /// orientation by construction.
    ///
    ///     grip.rotation = controllerWorld.rotation' * animatedHandWorld.rotation
    ///     grip.origin   = a small offset, nought by default
    ///
    /// THE TWO MODES, AND WHY THERE ARE TWO
    ///
    /// AUTOMATIC answers the orientation question, by watching the animation over
    /// a couple of seconds and AVERAGING it rather than taking one frame. That
    /// matters: a single frame taken mid-swing gives a mid-swing grip, which is
    /// how the first version could be run twice in a row and disagree with itself.
    /// Averaging also produces a number the single-frame version could not - how
    /// much the animation WOBBLED across the window - and that is the figure that
    /// says whether to trust the result. A character standing still gives a degree
    /// or two; one walking gives tens. It is reported rather than swallowed.
    ///
    /// MANUAL answers a question the game has no opinion about: how YOU hold a
    /// controller. It moves the hand under the sticks while you watch it.
    ///
    /// TWO HANDS, TWO GRIPS
    ///
    /// l_hand and r_hand are mirror images, and so are the OpenXR grip poses - the
    /// spec defines the palm normal as pointing away from the palm on the left and
    /// into it on the right. Two mirrored conventions compose into a relationship
    /// that is not the same on both sides, so each hand carries its own grip. The
    /// automatic pass samples both at once, since both bones and both controllers
    /// are in hand on any frame where either is.
    /// </summary>
    public static class VrHandCalibration
    {
        /// <summary>Key that opens the panel on the calibration tab and starts the
        /// manual mode. Kept as a shortcut now that the panel owns the UI.</summary>
        private static readonly string ToggleKeyName =
            VrConfig.String("grip_calibrate_key", "Home");

        private static readonly float MoveRate = VrConfig.Float("grip_move_rate", 0.20f);
        private static readonly float TurnRate = VrConfig.Float("grip_turn_rate", 60f);

        /// <summary>How long the automatic pass watches the animation. Long enough
        /// to average out a walk cycle, short enough that standing still for it is
        /// not a chore.</summary>
        private static readonly float AutoSeconds =
            Math.Max(0.25f, VrConfig.Float("grip_auto_seconds", 2.0f));

        /// <summary>Wobble past which the automatic result is called unreliable and
        /// the player is told to stand still and run it again.</summary>
        private static readonly float SpreadWarnDeg =
            VrConfig.Float("grip_auto_warn_degrees", 12f);

        /// <summary>Where the hand bone sits relative to the controller's grip
        /// pose, in the controller's own frame. Small by definition - see the class
        /// note - and zero unless someone has a reason.</summary>
        private static readonly Vec3 DefaultOffset = new Vec3(
            VrConfig.Float("grip_offset_x", 0f),
            VrConfig.Float("grip_offset_y", 0f),
            VrConfig.Float("grip_offset_z", 0f), -1f);

        private const int Roles = 2;

        // THE GRIP IS TWO THINGS, STORED SEPARATELY
        //
        // _captured is what the automatic pass measured: the rotation between the
        // controller and the hand, which cannot be described by anyone and has to
        // be observed. It stays a Mat3 and is never turned into angles.
        //
        // _offset and _euler are what the player adjusts: where the hand sits and
        // any turn on top. These are what the sliders show, and they are the six
        // numbers a person can reason about - "four centimetres too far forward"
        // is a sentence; a basis vector is not.
        //
        // Keeping them apart is what lets the sliders exist at all. Composed into
        // one matrix, the only way to offer an angle slider would be to extract
        // Euler angles from it every frame and put them back - which introduces a
        // convention, a gimbal case, and a value that creeps every time it makes
        // the round trip.
        private static readonly Mat3[] _captured = new Mat3[Roles];
        private static readonly Vec3[] _offset = new Vec3[Roles];
        private static readonly Vec3[] _euler = new Vec3[Roles];   // yaw, pitch, roll (deg)
        private static readonly bool[] _have = new bool[Roles];

        /// <summary>Whether this role's grip came from the config or from a save,
        /// rather than from an automatic pass nobody asked for. An unrequested
        /// capture is a guess, and a guess should be re-made each mission rather
        /// than carried.</summary>
        private static readonly bool[] _persisted = new bool[Roles];

        private static readonly Mat3[] _beforeRot = new Mat3[Roles];
        private static readonly Vec3[] _beforeOffset = new Vec3[Roles];
        private static readonly Vec3[] _beforeEuler = new Vec3[Roles];

        // --- the automatic pass ------------------------------------------------
        private static readonly bool[] _sampling = new bool[Roles];
        private static readonly Vec3[] _sumS = new Vec3[Roles];
        private static readonly Vec3[] _sumF = new Vec3[Roles];
        private static readonly Vec3[] _sumU = new Vec3[Roles];
        private static readonly int[] _count = new int[Roles];
        private static float _autoElapsed;
        private static bool _autoPersist;
        private static float _spreadDeg;

        /// <summary>
        /// An automatic pass is owed, but has nothing to sample yet.
        ///
        /// It cannot simply be started here. There is no agent during a loading
        /// screen and no controller pose before the session is running, so a pass
        /// begun on a timer would spend its whole window seeing nothing and then
        /// report that it saw nothing. VrWeaponHands starts it instead, on the
        /// first frame it has a skeleton and a tracked controller in hand - which
        /// is exactly the first frame it could succeed.
        /// </summary>
        private static bool _wantAuto = true;

        private static BvrCalibState _state = BvrCalibState.Idle;
        private static bool _loggedReady;
        private static HandRole _editing = HandRole.Main;

        /// <summary>What the panel should be showing.</summary>
        public static BvrCalibState State => _state;

        /// <summary>Which hand the manual mode is editing.</summary>
        public static HandRole Editing => _editing;

        /// <summary>0..1 through the automatic window.</summary>
        public static float Progress =>
            _state == BvrCalibState.Auto ? Math.Min(_autoElapsed / AutoSeconds, 1f) : 0f;

        /// <summary>How much the animation moved across the last automatic pass, in
        /// degrees. Zero before the first one.</summary>
        public static float SpreadDegrees => _spreadDeg;

        /// <summary>Bit 0 = the weapon hand is calibrated, bit 1 = the shield
        /// hand. Packed because that is how it crosses to the panel.</summary>
        public static int HaveMask => (_have[0] ? 1 : 0) | (_have[1] ? 2 : 0);

        /// <summary>True whenever a calibration is running, in either mode.
        /// VrGamepad reads this and goes quiet - the sticks are moving a hand
        /// during manual, and during automatic the player has been asked to hold
        /// still, which a stray nudge would undo.</summary>
        public static bool Capturing => _state != BvrCalibState.Idle;

        /// <summary>True while this hand must be LEFT ALONE so the animation's own
        /// orientation can be read off it. Per hand rather than global, so a hand
        /// that is already calibrated keeps tracking while the other one samples.
        /// </summary>
        public static bool SamplingFor(HandRole role) => _sampling[(int)role];

        /// <summary>True when a hand is owed an automatic pass and the caller has
        /// something to sample. See <see cref="_wantAuto"/>.</summary>
        public static bool WantsAuto => _wantAuto && _state == BvrCalibState.Idle;

        static VrHandCalibration()
        {
            for (int i = 0; i < Roles; i++)
            {
                _captured[i] = Mat3.Identity;
                _offset[i] = Vec3.Zero;
                _euler[i] = Vec3.Zero;
            }
        }

        /// <summary>
        /// Where this hand sits relative to the controller, in the BODY's frame -
        /// the slider offset, measured along the character's own left/right,
        /// forward and up.
        ///
        /// BODY FRAME, NOT THE CONTROLLER'S, AND THIS IS THE WHOLE POINT
        ///
        /// The first version added this inside the grip, in the controller's own
        /// frame. That is right for the thing the grip is FOR - the couple of
        /// centimetres between the OpenXR palm centroid and the wrist bone travels
        /// with your wrist, so it should rotate with it.
        ///
        /// It is wrong for what a player is actually asking when they say the
        /// hands are not where their hands are. That complaint is about a
        /// direction in the world - too low, too far forward - and an offset in
        /// the controller's frame does not have stable directions: roll your wrist
        /// and "up" becomes "sideways", so the slider labelled Z stops meaning up
        /// the moment you turn your hand over.
        ///
        /// The CyberpunkVR mod's arm IK lands in the same place. Its per-hand
        /// offset (g_VROffRX/Y/Z) is added in MODEL space, after the controller
        /// position has been rotated into the body frame:
        ///
        ///     outTarget = eyeAnchor + mapLocal + off
        ///
        /// and its calibration panel carries a separate vertical "Height" slider
        /// with an asymmetric -0.20..+0.50 m range - which is only a meaningful
        /// control if "up" reliably means up.
        /// </summary>
        public static Vec3 BodyOffset(HandRole role)
        {
            return _offset[(int)role];
        }

        /// <summary>The slider values for one hand: metres then degrees.</summary>
        public static void Adjustment(HandRole role, out Vec3 offset, out Vec3 euler)
        {
            int i = (int)role;
            offset = _offset[i];
            euler = _euler[i];
        }

        /// <summary>Applies what the sliders say.</summary>
        public static void SetAdjustment(HandRole role, Vec3 offset, Vec3 euler)
        {
            int i = (int)role;
            _offset[i] = offset;
            _euler[i] = euler;

            // A hand being adjusted is a hand that has an answer, even if the
            // automatic pass never ran. Otherwise moving a slider would do
            // nothing visible, because the poser skips a hand with no grip.
            _have[i] = true;
        }

        /// <summary>
        /// The controller-space frame a hand is posed with, composed from the
        /// captured rotation and the adjustment. False means nothing is known yet,
        /// and the caller must leave that hand on its animation - posing through a
        /// default grip would put the hand at the controller with an arbitrary
        /// rotation, which looks like a tracking fault rather than an uncalibrated
        /// one.
        /// </summary>
        public static bool TryGrip(HandRole role, out MatrixFrame grip)
        {
            int i = (int)role;

            grip = MatrixFrame.Identity;
            grip.rotation = _captured[i];

            // The adjustment turns the hand about its OWN axes, applied after the
            // capture. That is what makes a slider predictable: "yaw" means turn
            // the hand you can see, not turn it about some world axis it happens
            // to be at an angle to.
            Vec3 e = _euler[i];

            if (Math.Abs(e.x) > 0.01f)
                grip.rotation.RotateAboutUp(e.x * Deg2Rad);
            if (Math.Abs(e.y) > 0.01f)
                grip.rotation.RotateAboutSide(e.y * Deg2Rad);
            if (Math.Abs(e.z) > 0.01f)
                grip.rotation.RotateAboutForward(e.z * Deg2Rad);

            // The controller-frame part only: the wrist-versus-palm couple of
            // centimetres, which travels with your wrist and so belongs here. The
            // slider offset is in the BODY's frame and is applied by the caller -
            // see BodyOffset.
            grip.origin = DefaultOffset;
            return _have[i];
        }

        private const float Deg2Rad = 0.0174532925f;

        public static void Load()
        {
            LoadOne(HandRole.Main);
            LoadOne(HandRole.Off);
            Migrate();
        }

        private static bool LoadOne(HandRole role)
        {
            int i = (int)role;

            // The adjustment loads independently of the rotation. They are written
            // together, but a player who hand-edits one line should not lose the
            // other to a typo - and an adjustment on its own is perfectly usable,
            // because the automatic pass will fill in the rotation.
            string adj = VrConfig.String(AdjKey(role), string.Empty);
            if (!string.IsNullOrEmpty(adj))
            {
                if (TryParseAdjust(adj, out Vec3 offset, out Vec3 euler))
                {
                    _offset[i] = offset;
                    _euler[i] = euler;
                }
                else
                {
                    VrLog.Warn("Grip adjustment '" + AdjKey(role) + "' could not be read "
                               + "(expected 6 numbers, got '" + adj + "'). That hand starts at "
                               + "the controller instead.");
                }
            }

            string raw = VrConfig.String(RotKey(role), string.Empty);
            if (string.IsNullOrEmpty(raw))
                return false;

            if (!TryParseRotation(raw, out Mat3 rotation))
            {
                VrLog.Warn("Grip rotation '" + RotKey(role) + "' could not be read (expected 9 "
                           + "numbers, got '" + raw + "'). That hand will take its orientation "
                           + "from the animation instead.");
                return false;
            }

            Set(role, rotation, persisted: true);
            VrLog.Info("Grip calibration loaded for the " + role + " hand; it starts where you "
                       + "last left it. " + ToggleKeyName + " opens the calibration panel.");
            return true;
        }

        /// <summary>
        /// Reads the two older formats this has been through.
        ///
        /// Both stored a whole MatrixFrame. Only its ROTATION is worth carrying:
        /// the origin is the arbitrary controller-to-arm gap described in the
        /// class note, and importing it would put the hand back where the
        /// complaint came from.
        /// </summary>
        private static void Migrate()
        {
            MigrateFrame(HandRole.Main, "grip_calibration_main");
            MigrateFrame(HandRole.Off, "grip_calibration_off");
            MigrateFrame(HandRole.Main, "grip_calibration");
        }

        private static void MigrateFrame(HandRole role, string key)
        {
            if (_have[(int)role])
                return;

            string raw = VrConfig.String(key, string.Empty);
            if (string.IsNullOrEmpty(raw) || !TryParseFrame(raw, out MatrixFrame old))
                return;

            Set(role, old.rotation, persisted: true);

            VrLog.Info("Migrated '" + key + "' for the " + role + " hand: its rotation is kept, "
                       + "its offset discarded. The offset it stored was the gap between your "
                       + "controller and the character's arm at one instant of one animation, "
                       + "which is why the hand floated. Use the sliders if it needs moving.");
        }

        // =====================================================================
        //  AUTOMATIC
        // =====================================================================

        /// <summary>
        /// Watches the animation for a couple of seconds and averages what it sees.
        ///
        /// <paramref name="persist"/> separates the two callers. A player asking
        /// for this from the panel wants an answer that survives the session, so it
        /// is saved. The implicit pass at the start of a mission is filling in a
        /// gap, and writing that to the config would turn "we had to guess" into
        /// "this is what you chose".
        /// </summary>
        public static void StartAuto(bool persist, bool onlyMissing = false)
        {
            _state = BvrCalibState.Auto;
            _autoElapsed = 0f;
            _autoPersist = persist;
            _spreadDeg = 0f;
            _wantAuto = false;

            for (int i = 0; i < Roles; i++)
            {
                // onlyMissing is what keeps the implicit mission-start pass from
                // overwriting a saved calibration. A player asking for this from
                // the panel means both hands; a mission filling in a gap means
                // only the gap.
                _sampling[i] = !onlyMissing || !_have[i];
                _count[i] = 0;
                _sumS[i] = Vec3.Zero;
                _sumF[i] = Vec3.Zero;
                _sumU[i] = Vec3.Zero;
            }

            if (!_sampling[0] && !_sampling[1])
            {
                _state = BvrCalibState.Idle;
                return;
            }

            VrLog.Info("Grip: automatic calibration started. Stand in a neutral pose and hold "
                       + "the controllers as if they were the hilt and the shield strap for "
                       + AutoSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " seconds. "
                       + "Only the ORIENTATION is taken, so where your arms are does not matter.");
        }

        /// <summary>
        /// One frame's observation of where the animation has a hand, relative to
        /// the controller driving it. Called by VrWeaponHands while that hand is
        /// sampling, with both frames measured in the same world.
        ///
        /// The translation is deliberately not accumulated. See the class note: it
        /// is the gap between the player's arm and the character's, which is not a
        /// property of anything and was the whole of the first bug.
        /// </summary>
        public static void Observe(HandRole role, MatrixFrame controllerWorld,
                                   MatrixFrame animatedHandWorld)
        {
            int i = (int)role;
            if (!_sampling[i])
                return;

            // TransformToLocal is composition with the inverse, so the rotation of
            // this is exactly controllerRotation' * handRotation.
            MatrixFrame rel = controllerWorld.TransformToLocal(animatedHandWorld);

            // A bone's entitial frame can carry SCALE, and TransformToLocal will
            // happily hand back a basis that is not orthonormal. Normalising here
            // rather than only at the end keeps a scaled frame from dominating the
            // average by sheer length.
            rel.rotation.Orthonormalize();

            _sumS[i] += rel.rotation.s;
            _sumF[i] += rel.rotation.f;
            _sumU[i] += rel.rotation.u;
            _count[i]++;
        }

        private static void FinishAuto()
        {
            float worst = 0f;
            bool any = false;

            for (int i = 0; i < Roles; i++)
            {
                _sampling[i] = false;

                if (_count[i] == 0)
                    continue;

                any = true;

                // The mean of a set of rotations, cheaply: average each basis
                // vector and re-orthonormalise. Exact quaternion averaging would be
                // more principled, but the samples here are all within a few
                // degrees of one another by the time anyone trusts the result, and
                // over that range the two agree to well under the precision anybody
                // can perceive in a headset.
                Mat3 rotation = default;
                rotation.s = _sumS[i];
                rotation.f = _sumF[i];
                rotation.u = _sumU[i];
                rotation.Orthonormalize();

                float spread = SpreadOf(_sumF[i], _count[i]);
                if (spread > worst)
                    worst = spread;

                // Only the CAPTURED half is replaced. The offset and the angle
                // sliders are the player's, and an automatic pass silently
                // undoing them would make the two modes fight over the same hand.
                Set((HandRole)i, rotation, _autoPersist);

                if (_autoPersist)
                    SaveOne((HandRole)i);

                VrLog.Info("Grip captured for the " + (HandRole)i + " hand from " + _count[i]
                           + " samples, wobble " + spread.ToString("0.0", CultureInfo.InvariantCulture)
                           + " deg: " + Describe((HandRole)i));
            }

            _spreadDeg = worst;
            _state = BvrCalibState.Idle;

            if (!any)
            {
                VrLog.Warn("Grip: automatic calibration saw no frames - no agent, or neither "
                           + "controller was tracking. Nothing was changed.");
                return;
            }

            if (worst > SpreadWarnDeg)
            {
                VrLog.Warn("Grip: the animation moved " + worst.ToString("0", CultureInfo.InvariantCulture)
                           + " degrees during the capture, so the average is not worth much - "
                           + "you were walking or swinging. Stand still and run it again.");
            }
            else if (_autoPersist)
            {
                VrLog.Info("Grip: automatic calibration done and saved.");
            }
        }

        /// <summary>
        /// How much a set of unit vectors disagreed, in degrees.
        ///
        /// The resultant length over the count is the standard circular measure of
        /// agreement: identical samples sum to exactly the count, and scattered
        /// ones cancel. It falls out of the same accumulator the mean needs, so
        /// the honesty of the result costs nothing and no second pass.
        /// </summary>
        private static float SpreadOf(Vec3 sum, int count)
        {
            if (count <= 1)
                return 0f;

            double r = sum.Length / count;

            if (r >= 0.999999)
                return 0f;
            if (r <= 1e-6)
                return 180f;

            double radians = Math.Sqrt(-2.0 * Math.Log(r));
            return (float)Math.Min(radians * (180.0 / Math.PI), 180.0);
        }

        // =====================================================================
        //  MANUAL
        // =====================================================================

        public static void OpenManual()
        {
            if (_state == BvrCalibState.Manual)
                return;

            CancelAuto(quiet: true);

            _state = BvrCalibState.Manual;

            for (int i = 0; i < Roles; i++)
            {
                _beforeRot[i] = _captured[i];
                _beforeOffset[i] = _offset[i];
                _beforeEuler[i] = _euler[i];
            }

            VrLog.Info("Grip calibration OPEN, editing the " + _editing + " hand. Movement is "
                       + "suspended. The panel has the sliders - three for where the hand sits "
                       + "and three for how it is turned. On the controllers: A saves, B "
                       + "cancels, X runs the automatic pass, Y resets this hand, left menu "
                       + "switches hands.");
        }

        public static void Select(HandRole role)
        {
            if (_editing == role)
                return;

            _editing = role;
            VrLog.Info("Grip calibration now editing the " + role + " hand ("
                       + (role == HandRole.Main ? "the weapon hand" : "the shield hand") + ").");
        }

        /// <summary>Saves both hands and closes. Both, because the manual mode can
        /// switch between them and closing having saved only the last one touched
        /// would silently throw away the other.</summary>
        public static void Accept()
        {
            if (_state == BvrCalibState.Auto)
            {
                // Accepting mid-sample means "take what you have now" rather than
                // "throw it away", which is what a player who has stood still long
                // enough and wants to move on is asking for.
                FinishAuto();
                return;
            }

            if (_state != BvrCalibState.Manual)
                return;

            _state = BvrCalibState.Idle;

            SaveOne(HandRole.Main);
            SaveOne(HandRole.Off);
            VrLog.Info("Grip calibration SAVED for both hands.");
        }

        public static void Cancel()
        {
            if (_state == BvrCalibState.Auto)
            {
                CancelAuto(quiet: false);
                return;
            }

            if (_state != BvrCalibState.Manual)
                return;

            _state = BvrCalibState.Idle;

            for (int i = 0; i < Roles; i++)
            {
                _captured[i] = _beforeRot[i];
                _offset[i] = _beforeOffset[i];
                _euler[i] = _beforeEuler[i];
            }

            VrLog.Info("Grip calibration cancelled; both hands back to where they were.");
        }

        private static void CancelAuto(bool quiet)
        {
            if (_state != BvrCalibState.Auto)
                return;

            for (int i = 0; i < Roles; i++)
                _sampling[i] = false;

            _state = BvrCalibState.Idle;

            if (!quiet)
                VrLog.Info("Grip: automatic calibration cancelled; nothing was changed.");
        }

        /// <summary>Puts the edited hand back on the controller's own frame - no
        /// captured rotation and every slider at nought. The escape hatch from a
        /// capture that came out nonsensical, and visibly wrong rather than subtly
        /// so, which is what makes it a usable starting point for the sliders.
        /// </summary>
        public static void ResetEditing()
        {
            int i = (int)_editing;
            _offset[i] = Vec3.Zero;
            _euler[i] = Vec3.Zero;
            Set(_editing, Mat3.Identity, persisted: false);

            VrLog.Info("Grip for the " + _editing + " hand reset to the controller's own frame. "
                       + "Run the automatic pass if that was not what you wanted.");
        }

        private static void Set(HandRole role, Mat3 rotation, bool persisted)
        {
            int i = (int)role;
            _captured[i] = rotation;
            _have[i] = true;
            _sampling[i] = false;

            if (persisted)
                _persisted[i] = true;
        }

        private static string RotKey(HandRole role)
        {
            return role == HandRole.Main ? "grip_rotation_main" : "grip_rotation_off";
        }

        private static string AdjKey(HandRole role)
        {
            return role == HandRole.Main ? "grip_adjust_main" : "grip_adjust_off";
        }

        /// <summary>
        /// Writes one hand out as two lines rather than one.
        ///
        /// The captured rotation is nine numbers nobody should read; the
        /// adjustment is six a player might well want to check or copy between
        /// installs. Splitting them keeps the second legible, and it means an
        /// automatic pass rewrites only the line it actually measured.
        /// </summary>
        private static void SaveOne(HandRole role)
        {
            int i = (int)role;
            if (!_have[i])
                return;

            VrConfig.Save(RotKey(role), Serialise(_captured[i]));
            VrConfig.Save(AdjKey(role), SerialiseAdjust(_offset[i], _euler[i]));
            _persisted[i] = true;
        }

        // =====================================================================
        //  PER-FRAME
        // =====================================================================

        /// <summary>
        /// The shortcut key, the automatic window's clock, and the sticks while the
        /// manual mode is open.
        /// </summary>
        public static void Tick(float dt)
        {
            if (!_loggedReady)
            {
                _loggedReady = true;
                VrLog.Info("Motion controller calibration lives in the VR panel, under "
                           + "Calibration - automatic finds the orientation, manual adjusts how "
                           + "it sits in your hand. " + ToggleKeyName + " opens the panel there "
                           + "and starts the manual mode.");
            }

            if (VrKeys.Pressed(ToggleKeyName))
            {
                if (_state == BvrCalibState.Manual)
                {
                    Accept();
                }
                else
                {
                    // Opens the panel ON calibration rather than merely starting a
                    // hidden mode. The whole point of merging this into the panel
                    // is that the player can see which hand is being edited and
                    // what the sticks do.
                    NativeMethods.bvr_overlay_show(1);
                    OpenManual();
                }
            }

            if (_state == BvrCalibState.Auto)
            {
                _autoElapsed += dt;
                if (_autoElapsed >= AutoSeconds)
                    FinishAuto();

                return;     // the sticks are not nudging anything during a sample
            }

            if (_state != BvrCalibState.Manual)
                return;

            if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                return;

            Nudge(input, dt);
        }

        /// <summary>
        /// Moves the edited grip by whatever the sticks are doing.
        ///
        /// Rotation is applied about the GRIP's own axes and translation along the
        /// CONTROLLER's, which sounds inconsistent and is the thing that makes it
        /// feel right: turning a sword means turning it about itself, and moving it
        /// means moving it relative to the hand holding it.
        /// </summary>
        private static void Nudge(BvrInputState input, float dt)
        {
            BvrHandState right = input.Right;
            BvrHandState left = input.Left;

            if (right.IsActive == 0 && left.IsActive == 0)
                return;

            // -- buttons, edge-triggered --------------------------------------
            // Duplicates of the panel's own buttons, on purpose. The panel is
            // driven with the desktop mouse, and the whole reason to be in this
            // mode is that both your hands are holding controllers.
            if (Pressed(right, BvrButton.Primary, ref _prevSave))          // A
            {
                Accept();
                return;
            }

            if (Pressed(right, BvrButton.Secondary, ref _prevCancel))      // B
            {
                Cancel();
                return;
            }

            if (Pressed(left, BvrButton.Primary, ref _prevAlign))          // X
            {
                StartAuto(persist: true);
                return;
            }

            if (Pressed(left, BvrButton.Secondary, ref _prevReset))        // Y
            {
                ResetEditing();
                return;
            }

            if (Pressed(left, BvrButton.Menu, ref _prevSwitch))            // left menu
            {
                Select(_editing == HandRole.Main ? HandRole.Off : HandRole.Main);
                return;
            }

            // -- axes ----------------------------------------------------------
            //
            // The sticks move the SLIDERS now, rather than rotating a matrix
            // directly. That is the whole difference between the two: a stick used
            // to be a rate applied to a hidden transform, so there was no way to
            // see how far you had gone, no way back to nought, and two people
            // holding it for different lengths of time got different answers to
            // the same question. Driving the same six numbers the panel shows
            // means the sticks and the sliders cannot disagree, and letting go
            // leaves a value you can read.
            int e = (int)_editing;

            float move = MoveRate * dt;
            float turn = TurnRate * dt;

            Vec3 offset = _offset[e];
            Vec3 euler = _euler[e];
            bool moved = false;

            // Left grip held turns the hand; otherwise the sticks slide it. The
            // position is what people reach for, so it gets the unmodified sticks.
            if (left.Grip >= 0.5f)
            {
                if (Math.Abs(right.ThumbstickX) > 0.2f)
                {
                    euler.x = Clamp(euler.x + right.ThumbstickX * turn, -180f, 180f);
                    moved = true;
                }

                if (Math.Abs(right.ThumbstickY) > 0.2f)
                {
                    euler.y = Clamp(euler.y + right.ThumbstickY * turn, -180f, 180f);
                    moved = true;
                }

                if (Math.Abs(left.ThumbstickX) > 0.2f)
                {
                    euler.z = Clamp(euler.z + left.ThumbstickX * turn, -180f, 180f);
                    moved = true;
                }
            }
            else
            {
                if (Math.Abs(left.ThumbstickX) > 0.2f)
                {
                    offset.x = Clamp(offset.x + left.ThumbstickX * move, -0.5f, 0.5f);
                    moved = true;
                }

                if (Math.Abs(left.ThumbstickY) > 0.2f)
                {
                    offset.y = Clamp(offset.y + left.ThumbstickY * move, -0.5f, 0.5f);
                    moved = true;
                }

                float lift = right.Trigger - left.Trigger;
                if (Math.Abs(lift) > 0.05f)
                {
                    offset.z = Clamp(offset.z + lift * move, -0.5f, 0.5f);
                    moved = true;
                }
            }

            if (moved)
                SetAdjustment(_editing, offset, euler);
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static bool _prevSave, _prevCancel, _prevAlign, _prevReset, _prevSwitch;

        private static bool Pressed(BvrHandState hand, BvrButton button, ref bool prev)
        {
            bool now = hand.IsActive != 0 && (hand.Buttons & (int)button) != 0;
            bool edge = now && !prev;
            prev = now;
            return edge;
        }

        // --- persistence ------------------------------------------------------
        //
        // Twelve numbers rather than a position and three angles, because a Mat3
        // round-trips exactly and Euler angles do not: extracting them introduces a
        // choice of convention and a gimbal case, and a calibration that shifts
        // slightly every time it is saved and reloaded is worse than one that is
        // hard to read. Nobody is meant to edit these lines by hand - that is what
        // the panel is for.


        private static string Serialise(Mat3 r)
        {
            return Join(new[]
            {
                r.s.x, r.s.y, r.s.z,
                r.f.x, r.f.y, r.f.z,
                r.u.x, r.u.y, r.u.z,
            });
        }

        private static string SerialiseAdjust(Vec3 offset, Vec3 euler)
        {
            return Join(new[]
            {
                offset.x, offset.y, offset.z,
                euler.x, euler.y, euler.z,
            });
        }

        private static string Join(float[] v)
        {
            var parts = new string[v.Length];
            for (int i = 0; i < v.Length; i++)
                parts[i] = v[i].ToString("0.######", CultureInfo.InvariantCulture);

            return string.Join(",", parts);
        }

        private static bool Numbers(string raw, int count, out float[] v)
        {
            v = null;

            if (string.IsNullOrEmpty(raw))
                return false;

            string[] parts = raw.Split(',');
            if (parts.Length != count)
                return false;

            var parsed = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out parsed[i]))
                {
                    return false;
                }
            }

            v = parsed;
            return true;
        }

        private static bool TryParseRotation(string raw, out Mat3 r)
        {
            r = Mat3.Identity;

            if (!Numbers(raw, 9, out float[] v))
                return false;

            r.s = new Vec3(v[0], v[1], v[2], -1f);
            r.f = new Vec3(v[3], v[4], v[5], -1f);
            r.u = new Vec3(v[6], v[7], v[8], -1f);
            return true;
        }

        private static bool TryParseAdjust(string raw, out Vec3 offset, out Vec3 euler)
        {
            offset = Vec3.Zero;
            euler = Vec3.Zero;

            if (!Numbers(raw, 6, out float[] v))
                return false;

            offset = new Vec3(v[0], v[1], v[2], -1f);
            euler = new Vec3(v[3], v[4], v[5], -1f);
            return true;
        }

        /// <summary>The 12-number format the two older config keys used. Read only
        /// - see Migrate.</summary>
        private static bool TryParseFrame(string raw, out MatrixFrame f)
        {
            f = MatrixFrame.Identity;

            if (!Numbers(raw, 12, out float[] v))
                return false;

            f.rotation.s = new Vec3(v[0], v[1], v[2], -1f);
            f.rotation.f = new Vec3(v[3], v[4], v[5], -1f);
            f.rotation.u = new Vec3(v[6], v[7], v[8], -1f);
            f.origin = new Vec3(v[9], v[10], v[11], -1f);
            return true;
        }

        private static string Describe(HandRole role)
        {
            int i = (int)role;
            Mat3 r = _captured[i];
            Vec3 o = _offset[i];

            return string.Format(CultureInfo.InvariantCulture,
                "the hand points along ({0:0.00}, {1:0.00}, {2:0.00}) in the controller's frame, "
                + "offset ({3:0.000}, {4:0.000}, {5:0.000}) m",
                r.f.x, r.f.y, r.f.z, o.x, o.y, o.z);
        }

        /// <summary>
        /// Per-mission reset.
        ///
        /// A grip that was captured without being asked for is a guess taken at
        /// whatever instant the last mission started, so it is re-made rather than
        /// carried into the next one. A grip that was SAVED is an answer, and is
        /// kept. If anything needs re-making, the automatic pass runs on its own -
        /// which is also what makes a fresh install work without touching a menu.
        /// </summary>
        public static void Reset()
        {
            CancelAuto(quiet: true);
            _state = BvrCalibState.Idle;

            for (int i = 0; i < Roles; i++)
            {
                if (_persisted[i])
                    continue;

                _have[i] = false;
                _wantAuto = true;
            }
        }
    }
}
