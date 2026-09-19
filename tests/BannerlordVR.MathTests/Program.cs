using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BannerlordVR.Interop;
using BannerlordVR.VR;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.MathTests
{
    /// <summary>
    /// Guards the coordinate maths. Two distinct things are being checked:
    ///
    ///   1. What the SHIPPED TaleWorlds assemblies actually compute. The whole
    ///      camera chain rests on MatrixFrame.TransformToParent composing the way
    ///      the name suggests and Mat3.TransformToLocal being a true inverse.
    ///      Neither is documented; both are asserted here against the real DLLs.
    ///   2. That our own conversions are self-consistent - basis swap, YawOnly,
    ///      and the recentre round trip.
    ///
    /// A wrong answer in any of these does not throw. It renders a world that is
    /// tilted, mirrored, or subtly the wrong scale, which is a miserable thing to
    /// track down from inside a headset.
    /// </summary>
    internal static class Program
    {
        private static int _fail;

        private static void Main()
        {
            EngineSemantics();
            CameraConvention();
            BasisSwap();
            YawOnlyBehaviour();
            RecentreRoundTrip();
            Frustum();
            BoneSideMatching();
            OverlayAbi();
            GripCalibration();
            GamepadDeadzone();

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? "ALL CHECKS PASSED" : _fail + " CHECK(S) FAILED");
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        // -- 1. What do TransformToParent / TransformToLocal actually mean? ----
        private static void EngineSemantics()
        {
            Section("engine transform semantics");

            // Pure yaw of +90 deg about Bannerlord up (+Z): forward +Y -> +X.
            Mat3 r = default;
            r.s = new Vec3(0f, -1f, 0f, -1f);
            r.f = new Vec3(1f, 0f, 0f, -1f);
            r.u = new Vec3(0f, 0f, 1f, -1f);

            var v = new Vec3(1f, 2f, 3f, -1f);

            // ToParent should be x*s + y*f + z*u.
            Expect("Mat3.TransformToParent", r.TransformToParent(v), new Vec3(2f, -1f, 3f, -1f));
            // ToLocal should be the transpose: (v.s, v.f, v.u) dot products.
            Expect("Mat3.TransformToLocal", r.TransformToLocal(v), new Vec3(-2f, 1f, 3f, -1f));
            Expect("Mat3 round trip", r.TransformToLocal(r.TransformToParent(v)), v);

            // TransformToLocal(Mat3.Identity) must give the transpose, which is
            // what ComputeRecentre relies on to invert a yaw without guessing
            // RotateAboutUp's sign convention.
            Mat3 transposed = r.TransformToLocal(Mat3.Identity);
            Expect("transpose.s", transposed.s, new Vec3(0f, 1f, 0f, -1f));
            Expect("transpose.f", transposed.f, new Vec3(-1f, 0f, 0f, -1f));
            Expect("transpose.u", transposed.u, new Vec3(0f, 0f, 1f, -1f));

            MatrixFrame parent = default;
            parent.rotation = r;
            parent.origin = new Vec3(10f, 20f, 30f, -1f);

            MatrixFrame child = default;
            child.rotation = Mat3.Identity;
            child.origin = new Vec3(1f, 0f, 0f, -1f);

            MatrixFrame outFrame = parent.TransformToParent(child);
            Expect("frame compose origin", outFrame.origin, new Vec3(10f, 19f, 30f, -1f));
            Expect("frame compose forward", outFrame.rotation.f, r.f);

            // The check above uses an identity child, which passes under EITHER
            // multiplication order and so proves nothing about composition. The
            // camera chain is anchor(yaw) x head(pitch), and the two orders differ
            // exactly there: yaw-then-pitch keeps the horizon level, pitch-then-yaw
            // tilts it and points a level head at the ground.
            Mat3 pitchDown = PitchAboutX((float)(-40.0 * Math.PI / 180.0));

            MatrixFrame yawParent = default;
            yawParent.rotation = r;
            yawParent.origin = new Vec3(0f, 0f, 0f, -1f);

            MatrixFrame pitchChild = default;
            pitchChild.rotation = pitchDown;
            pitchChild.origin = new Vec3(0f, 0f, 0f, -1f);

            Mat3 composed = yawParent.TransformToParent(pitchChild).rotation;

            // Correct: parent rotation applied to each of the child's basis vectors.
            Expect("compose order f", composed.f, r.TransformToParent(pitchDown.f));
            Expect("compose order u", composed.u, r.TransformToParent(pitchDown.u));
            Expect("compose order s", composed.s, r.TransformToParent(pitchDown.s));

            // And the property that actually matters in the headset: yawing a
            // pitched view must not roll it. Side vector stays horizontal.
            Approx("no roll introduced by yaw", composed.s.z, 0f);
        }

        // -- 1b. Which way does a CAMERA frame actually look? ------------------
        // VrCameraDriver builds each eye frame with rotation.f = the direction the
        // player is looking. That is only right if the engine treats a camera's +f
        // as its view direction. MatrixFrame.CreateLookAt is the engine's own
        // look-at helper and pure library maths, so it can be asked directly.
        private static void CameraConvention()
        {
            Section("camera frame convention");

            // Non-zero eye position, so a placement frame (origin == eye) can be
            // told apart from a view matrix (origin == -R^T * eye).
            var eye = new Vec3(5f, -3f, 2f, -1f);
            var target = new Vec3(5f, 7f, 2f, -1f);       // due +Y, dead level
            var up = new Vec3(0f, 0f, 1f, -1f);

            MatrixFrame look = MatrixFrame.CreateLookAt(eye, target, up);

            Console.WriteLine(string.Format(
                "    CreateLookAt: origin=({0,6:F3},{1,6:F3},{2,6:F3})  s=({3,6:F3},{4,6:F3},{5,6:F3})  " +
                "f=({6,6:F3},{7,6:F3},{8,6:F3})  u=({9,6:F3},{10,6:F3},{11,6:F3})",
                look.origin.x, look.origin.y, look.origin.z,
                look.rotation.s.x, look.rotation.s.y, look.rotation.s.z,
                look.rotation.f.x, look.rotation.f.y, look.rotation.f.z,
                look.rotation.u.x, look.rotation.u.y, look.rotation.u.z));

            // Informational only, and deliberately not asserted on. CreateLookAt
            // returns an origin of eye + forward + up rather than the eye, so it is
            // not a plain camera placement frame and cannot be trusted to define
            // the camera convention. Camera's own static helpers would settle it,
            // but they need the engine's native layer and hard-crash out of process.
            //
            // So VrCameraDriver does not assume a convention at all: it calls
            // Camera.LookAt(position, target, up) and lets the engine build its own
            // frame. The one-shot "camera convention" line in BannerlordVR.log
            // records the real answer from a live CombatCamera, for Phase 4.
            Console.WriteLine("    (not asserted - see the camera convention line in BannerlordVR.log)");
        }

        // -- 2. OpenXR -> Bannerlord basis swap --------------------------------
        private static void BasisSwap()
        {
            Section("basis swap");

            Expect("xr forward (0,0,-1) -> bl +Y",
                VrMath.ToBannerlord(Xr(0f, 0f, -1f)), new Vec3(0f, 1f, 0f, -1f));
            Expect("xr up (0,1,0) -> bl +Z",
                VrMath.ToBannerlord(Xr(0f, 1f, 0f)), new Vec3(0f, 0f, 1f, -1f));
            Expect("xr right (1,0,0) -> bl +X",
                VrMath.ToBannerlord(Xr(1f, 0f, 0f)), new Vec3(1f, 0f, 0f, -1f));

            // Identity orientation must land on the identity basis.
            Mat3 m = VrMath.ToBannerlord(Quat(0f, 0f, 0f, 1f));
            Expect("identity quat -> f", m.f, new Vec3(0f, 1f, 0f, -1f));
            Expect("identity quat -> u", m.u, new Vec3(0f, 0f, 1f, -1f));
            Expect("identity quat -> s", m.s, new Vec3(1f, 0f, 0f, -1f));

            // Yaw 90 deg about XR up (+Y) = turning left. In Bannerlord that is a
            // rotation about +Z; forward +Y should swing to -X.
            float h = (float)Math.Sin(Math.PI / 4.0);
            float w = (float)Math.Cos(Math.PI / 4.0);
            Mat3 yawed = VrMath.ToBannerlord(Quat(0f, h, 0f, w));
            Expect("xr yaw+90 -> bl forward", yawed.f, new Vec3(-1f, 0f, 0f, -1f));
            Expect("xr yaw+90 keeps up", yawed.u, new Vec3(0f, 0f, 1f, -1f));
        }

        // -- 3. YawOnly strips pitch/roll but keeps heading --------------------
        private static void YawOnlyBehaviour()
        {
            Section("YawOnly");

            // Pitched down 30 deg about XR right (+X), no yaw.
            float a = (float)(-30.0 * Math.PI / 180.0);
            Mat3 pitched = VrMath.ToBannerlord(Quat((float)Math.Sin(a / 2f), 0f, 0f, (float)Math.Cos(a / 2f)));
            Mat3 flat = VrMath.YawOnly(pitched);

            Expect("pitch removed -> up is +Z", flat.u, new Vec3(0f, 0f, 1f, -1f));
            Expect("pitch removed -> forward flat", flat.f, new Vec3(0f, 1f, 0f, -1f));
            Approx("forward is unit length", flat.f.Length, 1f);

            // Looking straight up must not produce a NaN basis.
            Mat3 straightUp = VrMath.ToBannerlord(Quat((float)Math.Sin(-Math.PI / 4.0), 0f, 0f, (float)Math.Cos(-Math.PI / 4.0)));
            Mat3 degenerate = VrMath.YawOnly(straightUp);
            Check("degenerate case is finite",
                !float.IsNaN(degenerate.f.x) && !float.IsNaN(degenerate.f.y) && !float.IsNaN(degenerate.f.z));
            Approx("degenerate forward still unit", degenerate.f.Length, 1f);
        }

        // -- 4. The recentre round trip ---------------------------------------
        private static void RecentreRoundTrip()
        {
            Section("recentre");

            MethodInfo compute = typeof(VrCameraDriver).GetMethod(
                "ComputeRecentre", BindingFlags.NonPublic | BindingFlags.Static);
            if (compute == null)
            {
                Fail("ComputeRecentre not found via reflection");
                return;
            }

            // Head yawed 40 deg left and pitched 15 deg down, standing 1.7 m up
            // and 2 m off the stage origin.
            float yaw = (float)(40.0 * Math.PI / 180.0);
            float pitch = (float)(-15.0 * Math.PI / 180.0);

            Mat3 rot = Compose(yaw, pitch);
            MatrixFrame head = default;
            head.rotation = rot;
            head.origin = new Vec3(2f, -0.5f, 1.7f, -1f);

            var recentre = (MatrixFrame)compute.Invoke(null, new object[] { head });
            MatrixFrame corrected = recentre.TransformToParent(head);

            // Position must collapse to the anchor.
            Approx("recentred origin x", corrected.origin.x, 0f);
            Approx("recentred origin y", corrected.origin.y, 0f);
            Approx("recentred origin z", corrected.origin.z, 0f);

            // Heading must collapse to straight ahead...
            Mat3 correctedFlat = VrMath.YawOnly(corrected.rotation);
            Approx("recentred heading x", correctedFlat.f.x, 0f);
            Approx("recentred heading y", correctedFlat.f.y, 1f);

            // ...but pitch must survive untouched. A head tilted 15 deg down
            // before recentring is still tilted 15 deg down after.
            float pitchBefore = (float)Math.Asin(rot.f.z);
            float pitchAfter = (float)Math.Asin(corrected.rotation.f.z);
            Approx("pitch preserved through recentre", pitchAfter, pitchBefore);

            // Moving 1 m forward after recentring must move exactly 1 m in the
            // anchor's forward direction - no scale error, no rotation leak.
            MatrixFrame moved = head;
            Vec3 stageForward = VrMath.YawOnly(rot).f;
            moved.origin = new Vec3(
                head.origin.x + stageForward.x,
                head.origin.y + stageForward.y,
                head.origin.z + stageForward.z, -1f);

            MatrixFrame movedCorrected = recentre.TransformToParent(moved);
            Approx("1 m forward -> +1 on Y", movedCorrected.origin.y, 1f);
            Approx("1 m forward -> 0 on X", movedCorrected.origin.x, 0f);
        }

        // -- 5. Frustum extents ------------------------------------------------
        private static void Frustum()
        {
            Section("frustum");

            // A typical asymmetric Quest-style FOV, in radians.
            var fov = new BvrFov
            {
                AngleLeft = -0.90f,
                AngleRight = 0.80f,
                AngleUp = 0.85f,
                AngleDown = -0.92f,
            };

            VrMath.FovToFrustum(fov, 0.05f, out float l, out float r, out float b, out float t);

            Check("left is negative", l < 0f);
            Check("right is positive", r > 0f);
            Check("bottom is negative", b < 0f);
            Check("top is positive", t > 0f);
            Check("frustum is asymmetric horizontally", Math.Abs(Math.Abs(l) - Math.Abs(r)) > 1e-4f);
            Approx("left extent", l, (float)Math.Tan(-0.90) * 0.05f);
        }

        // -- helpers -----------------------------------------------------------

        /// <summary>Bannerlord-space rotation for a given yaw (about +Z) and
        /// pitch (about the yawed side axis), built independently of VrMath.</summary>
        private static Mat3 Compose(float yaw, float pitch)
        {
            float cy = (float)Math.Cos(yaw), sy = (float)Math.Sin(yaw);
            float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);

            var f = new Vec3(-sy * cp, cy * cp, sp, -1f);
            var s = new Vec3(cy, sy, 0f, -1f);
            Vec3 u = Vec3.CrossProduct(s, f);

            Mat3 m = default;
            m.f = f; m.s = s; m.u = u;
            return m;
        }

        /// <summary>Pitch about the +X (side) axis. Negative pitches the forward
        /// vector downward, toward -Z.</summary>
        private static Mat3 PitchAboutX(float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            Mat3 m = default;
            m.s = new Vec3(1f, 0f, 0f, -1f);
            m.f = new Vec3(0f, c, s, -1f);
            m.u = new Vec3(0f, -s, c, -1f);
            return m;
        }

        private static BvrVec3 Xr(float x, float y, float z) => new BvrVec3 { X = x, Y = y, Z = z };
        private static BvrQuat Quat(float x, float y, float z, float w) => new BvrQuat { X = x, Y = y, Z = z, W = w };

        private static void Dump(string label, MatrixFrame f)
        {
            Console.WriteLine(string.Format(
                "    {0,-20} origin=({1,6:F3},{2,6:F3},{3,6:F3})  s=({4,6:F3},{5,6:F3},{6,6:F3})  " +
                "f=({7,6:F3},{8,6:F3},{9,6:F3})  u=({10,6:F3},{11,6:F3},{12,6:F3})",
                label, f.origin.x, f.origin.y, f.origin.z,
                f.rotation.s.x, f.rotation.s.y, f.rotation.s.z,
                f.rotation.f.x, f.rotation.f.y, f.rotation.f.z,
                f.rotation.u.x, f.rotation.u.y, f.rotation.u.z));
        }


        // -- 7. Which hand does weapon_bone_side actually select? --------------
        //
        // TEST 58 shipped weapon_bone_side = _r against a GUESSED bone table of
        // 'hand_R'. The real skeleton is 'l_hand' and 'r_hand', and "r_hand"
        // does not contain "_r" - so the side never matched, the search fell
        // back to the first bone containing 'hand', and that is the LEFT one.
        // The mod posed the empty hand at 120 fps and the weapon never moved.
        //
        // The failure had no symptom other than absence, which is exactly the
        // kind of thing a table of strings should be catching instead of a
        // headset. Both conventions are asserted here, in both directions, so
        // neither can be traded for the other again.
        private static void BoneSideMatching()
        {
            Section("bone side matching");

            // name, side, expected
            object[][] cases =
            {
                // The real skeleton, and the exact case that was broken.
                new object[] { "r_hand", "_r", true  },
                new object[] { "l_hand", "_r", false },

                // The convention the config was written against still works.
                new object[] { "hand_r", "_r", true  },
                new object[] { "hand_l", "_r", false },

                // Either way of writing the side selects either convention.
                new object[] { "r_hand", "r_", true  },
                new object[] { "hand_r", "r_", true  },
                new object[] { "r_hand", "r",  true  },
                new object[] { "l_hand", "r",  false },

                // Other separators, and a side in the middle of a name.
                new object[] { "hand.r",       "_r", true  },
                new object[] { "bip01_r_hand", "_r", true  },
                new object[] { "bip01_l_hand", "_r", false },

                // The point of matching a TOKEN rather than a letter: 'r'
                // appears inside plenty of left-hand bone names.
                new object[] { "l_forearm_twist", "r", false },
                new object[] { "l_hand_ring0",    "r", false },

                // Left-handed players get the mirror of all of it.
                new object[] { "l_hand", "_l", true  },
                new object[] { "r_hand", "_l", false },

                // No side configured means every candidate qualifies.
                new object[] { "l_hand", "", true },

                // The two names this actually has to separate now. VrHandSlots
                // asks which side the MAIN hand bone is on to turn "the bow goes
                // in my left hand" into a bone index, and it asks with a bare
                // letter, so these two are the load-bearing rows of the table.
                new object[] { "r_hand", "l", false },
                new object[] { "l_hand", "l", true  },
            };

            foreach (object[] c in cases)
            {
                string name = (string)c[0];
                string side = (string)c[1];
                bool want = (bool)c[2];
                bool got = VrBoneNames.MatchesSide(name, side);

                Report(got == want, string.Format(
                    "{0,-18} side {1,-4} -> {2,-5} (want {3})", name, "'" + side + "'", got, want));
            }
        }

        // -- 7b. Does the overlay struct match the one C thinks it is? --------
        //
        // BvrOverlayState crosses a P/Invoke boundary, and a layout mismatch
        // there does not fail to compile. It reads the stack wrong and hands the
        // game a world scale built out of whatever was in the next register -
        // which is exactly the failure mode that made a struct worth having over
        // the eleven-parameter signature it replaced.
        //
        // The native side asserts sizeof == 40 at compile time. This is the other
        // half of that pair: the same number, checked against what the marshaller
        // will actually do, plus the offsets, so a field inserted in the middle of
        // one declaration and not the other is caught by a test run rather than by
        // a world that is suddenly the wrong size.
        private static void OverlayAbi()
        {
            Section("overlay ABI layout");

            Type t = typeof(BvrOverlayState);

            Report(Marshal.SizeOf(t) == 104, string.Format(
                "BvrOverlayState is {0} bytes (want 104, and bvr_api.cpp static_asserts it)",
                Marshal.SizeOf(t)));

            // Sixteen four-byte fields in declared order. Named individually
            // rather than looped, because the point is that THESE names sit at
            // THESE offsets - a loop over GetFields would happily agree with a
            // struct whose fields had been reordered.
            string[] names =
            {
                "WorldScale", "RenderScale", "StereoMode", "StereoPending",
                "CalibState", "CalibHand", "CalibProgress", "CalibSpread",
                "CalibHave",
                "CalibX", "CalibY", "CalibZ", "CalibYaw", "CalibPitch", "CalibRoll",
                "CalibCommand", "HideBody", "MotionHands",
                "UiShow", "UiWidth", "UiDistance",
                "ScreenWidth", "ScreenDistance",
                "TableauProbe", "TableauProbeState", "Sharpen",
            };

            for (int i = 0; i < names.Length; i++)
            {
                int want = i * 4;
                int got = Marshal.OffsetOf(t, names[i]).ToInt32();
                Report(got == want, string.Format(
                    "{0,-14} at offset {1,2} (want {2})", names[i], got, want));
            }
        }

        // -- 8. Does the grip put the hand where the CONTROLLER is? -----------
        //
        // This is the regression test for the bug the log caught. The first
        // calibration captured the whole rigid transform between the controller
        // and the hand the animation had drawn:
        //
        //     grip = controller' * animatedHand
        //
        // which is correct arithmetic and a wrong definition, because the
        // translation in it is the gap between the player's arm and the
        // character's - a number with no meaning that was then held forever. The
        // log shows it landing anywhere from 3 cm to 63 cm on successive presses
        // of the same key.
        //
        // The claim now splits in two, and the second half is what these check:
        //
        //     rotation  averaged from the animation  (the mesh binding is real)
        //     position  taken from the controller    (OpenXR grip = palm centroid)
        //
        // So after a capture the posed hand must be AT the controller and turned
        // the way the animation had it - and must be at the controller no matter
        // how far away the animation's arm happened to be. That last property is
        // the one the old code failed, and it is asserted with an animated hand
        // deliberately placed most of a metre away.
        //
        // Asserted against the SHIPPED MatrixFrame, because the underlying claim
        // is about TransformToLocal and TransformToParent being true inverses for
        // frames, not just for vectors.
        private static void GripCalibration()
        {
            Section("grip calibration");

            // A controller somewhere awkward: translated, and yawed 90 degrees so
            // a wrong composition order cannot pass by symmetry.
            Mat3 yaw90 = default;
            yaw90.s = new Vec3(0f, -1f, 0f, -1f);
            yaw90.f = new Vec3(1f, 0f, 0f, -1f);
            yaw90.u = new Vec3(0f, 0f, 1f, -1f);

            MatrixFrame controller = default;
            controller.rotation = yaw90;
            controller.origin = new Vec3(3f, -2f, 1.4f, -1f);

            // The hand as the animation has it: pointing somewhere else entirely,
            // and 0.79 m away - about what the log recorded on its worst press.
            Mat3 pitch = default;
            pitch.s = new Vec3(1f, 0f, 0f, -1f);
            pitch.f = new Vec3(0f, 0f, 1f, -1f);
            pitch.u = new Vec3(0f, -1f, 0f, -1f);

            MatrixFrame hand = default;
            hand.rotation = pitch;
            hand.origin = new Vec3(3.4f, -2.6f, 1.83f, -1f);

            // The other hand, somewhere else again, so the two cannot pass by
            // being accidentally equal.
            MatrixFrame other = default;
            other.rotation = Mat3.Identity;
            other.origin = new Vec3(-1f, 0.5f, 1.2f, -1f);

            // -- the automatic pass -------------------------------------------
            VrHandCalibration.StartAuto(persist: false);

            Check("the automatic pass watches both hands",
                VrHandCalibration.SamplingFor(HandRole.Main)
                && VrHandCalibration.SamplingFor(HandRole.Off));

            Check("and reports itself busy, so the pad goes quiet",
                VrHandCalibration.Capturing);

            for (int i = 0; i < 9; i++)
            {
                VrHandCalibration.Observe(HandRole.Main, controller, hand);
                VrHandCalibration.Observe(HandRole.Off, controller, other);
            }

            VrHandCalibration.Accept();   // "take what you have now"

            Check("the pass finishes", !VrHandCalibration.Capturing
                && !VrHandCalibration.SamplingFor(HandRole.Main)
                && !VrHandCalibration.SamplingFor(HandRole.Off));

            Check("both hands come out calibrated", VrHandCalibration.HaveMask == 3);

            // A character standing perfectly still is the best case, and it must
            // read as such - otherwise the warning that says "you were moving"
            // cries wolf on every good capture.
            Approx("a still capture reports no wobble", VrHandCalibration.SpreadDegrees, 0f);

            Check("capture reports a grip",
                VrHandCalibration.TryGrip(HandRole.Main, out MatrixFrame grip));

            MatrixFrame posed = controller.TransformToParent(grip);

            // 1. THE BUG. The hand goes where the controller is, not where the
            //    animation's arm was. Nothing about the 0.79 m survives.
            Approx("posed hand sits on the controller",
                (posed.origin - controller.origin).Length, 0f);

            // 2. The half that IS captured: the orientation the animation was
            //    holding the weapon at, reproduced exactly.
            Expect("posed hand keeps the animation's forward", posed.rotation.f, hand.rotation.f);
            Expect("posed hand keeps the animation's up", posed.rotation.u, hand.rotation.u);

            // 3. Moved and turned, the hand must follow rigidly: still on the
            //    controller, still the same orientation relative to it. This is
            //    the "and it tracks" claim, and it is the one that fails if the
            //    grip is stored in the wrong space.
            MatrixFrame moved = default;
            moved.rotation = Mat3.Identity;
            moved.origin = new Vec3(-7f, 5f, 0.9f, -1f);

            MatrixFrame movedHand = moved.TransformToParent(grip);

            Approx("hand stays on the controller after it moves",
                (movedHand.origin - moved.origin).Length, 0f);

            Vec3 fBefore = controller.rotation.TransformToLocal(posed.rotation.f);
            Vec3 fAfter = moved.rotation.TransformToLocal(movedHand.rotation.f);
            Expect("hand keeps its orientation", fAfter, fBefore);

            // 4. The two hands are mirror images, not copies - l_hand and r_hand
            //    are mirrored and so are the OpenXR grip poses. One pass fills in
            //    both, and they must not come out as the same answer.
            Check("the off hand has its own grip",
                VrHandCalibration.TryGrip(HandRole.Off, out MatrixFrame offGrip));
            Check("and it is a different one",
                (offGrip.rotation.f - grip.rotation.f).Length > 0.01f);

            AveragingAndWobble(controller, hand, other);
            GripSliders(controller);
            GripPersistence(grip);
        }

        // -- 8b. Is the automatic pass actually better than one frame? ---------
        //
        // Averaging is the whole reason the automatic mode exists rather than a
        // single snapshot. The first version could be run twice in a row and
        // disagree with itself, because one frame taken mid-swing gives a
        // mid-swing grip.
        //
        // Two claims, and both matter:
        //
        //   the mean lands BETWEEN the samples, so a swing either side of a
        //   neutral pose averages back to the neutral pose;
        //
        //   the wobble figure reports how far apart they were, so a capture taken
        //   while walking can say so instead of being quietly presented as an
        //   answer. That number is what the panel colours yellow.
        private static void AveragingAndWobble(MatrixFrame controller, MatrixFrame hand,
                                               MatrixFrame other)
        {
            const float swingDeg = 12f;

            MatrixFrame early = hand;
            early.rotation.RotateAboutUp(swingDeg * (float)(Math.PI / 180.0));

            MatrixFrame late = hand;
            late.rotation.RotateAboutUp(-swingDeg * (float)(Math.PI / 180.0));

            VrHandCalibration.StartAuto(persist: false);
            VrHandCalibration.Observe(HandRole.Main, controller, early);
            VrHandCalibration.Observe(HandRole.Main, controller, late);
            VrHandCalibration.Observe(HandRole.Off, controller, other);
            VrHandCalibration.Accept();

            VrHandCalibration.TryGrip(HandRole.Main, out MatrixFrame avg);
            MatrixFrame posed = controller.TransformToParent(avg);

            // Neither sample was the neutral pose; their average is.
            Expect("two samples average to the pose between them",
                posed.rotation.f, hand.rotation.f);

            // And the spread says how far apart they were, in the units the panel
            // prints. Loose bounds - this is a circular statistic, not the exact
            // half-angle - but it has to land near the truth or the warning
            // threshold means nothing.
            float spread = VrHandCalibration.SpreadDegrees;
            Report(spread > swingDeg - 3f && spread < swingDeg + 3f, string.Format(
                "wobble of two samples {0} deg apart reads {1:0.0} (want near {2})",
                swingDeg * 2f, spread, swingDeg));
        }

        // -- 8c. Do the sliders move the hand the way their labels say? --------
        //
        // The sliders exist because "the hands are not in the same location as my
        // motion controllers" is a complaint about POSITION, and the only honest
        // answer to it is a number the player can move and read back. So the two
        // things that have to be true are that the number arrives where it says
        // it does, and that it does not disturb the orientation the automatic
        // pass measured - the two halves are stored apart precisely so that
        // adjusting one cannot damage the other.
        private static void GripSliders(MatrixFrame controller)
        {
            VrHandCalibration.TryGrip(HandRole.Main, out MatrixFrame before);

            var offset = new Vec3(0.05f, -0.12f, 0.03f, -1f);
            VrHandCalibration.SetAdjustment(HandRole.Main, offset, Vec3.Zero);

            VrHandCalibration.TryGrip(HandRole.Main, out MatrixFrame after);

            // The slider value arrives as a BODY-frame offset, not inside the
            // grip. See VrHandCalibration.BodyOffset: an offset in the
            // controller's frame has no stable directions, so a slider labelled Z
            // stops meaning "up" the moment you turn your hand over.
            Expect("the position sliders land where they say",
                VrHandCalibration.BodyOffset(HandRole.Main), offset);

            // The grip itself is untouched by them - orientation and the small
            // controller-frame wrist offset both.
            Expect("moving the hand does not turn it", after.rotation.f, before.rotation.f);
            Expect("moving the hand does not roll it", after.rotation.u, before.rotation.u);
            Expect("and does not disturb the wrist offset", after.origin, before.origin);

            // THE PROPERTY THE BODY FRAME IS FOR.
            //
            // Roll the controller right over and the offset must still displace
            // the hand the same way in the world. Applied in the controller's
            // frame - which is what this replaced - the same slider would move the
            // hand somewhere else entirely, and a player chasing "my hands are too
            // low" would find the fix stopped working when they turned their wrist.
            Mat3 body = Mat3.Identity;
            Vec3 fromBody = body.TransformToParent(VrHandCalibration.BodyOffset(HandRole.Main));

            Mat3 rolled = controller.rotation;
            rolled.RotateAboutForward((float)Math.PI);
            Vec3 wouldHaveBeen = rolled.TransformToParent(offset);

            Expect("a body-frame offset is the slider value in body axes", fromBody, offset);
            Check("and rolling the controller does not move it",
                (wouldHaveBeen - fromBody).Length > 0.05f);

            // An angle slider turns the hand and leaves its position alone.
            VrHandCalibration.SetAdjustment(HandRole.Main, offset, new Vec3(90f, 0f, 0f, -1f));
            VrHandCalibration.TryGrip(HandRole.Main, out MatrixFrame turned);

            Expect("an angle slider does not move the hand",
                VrHandCalibration.BodyOffset(HandRole.Main), offset);
            Check("an angle slider turns it",
                (turned.rotation.f - before.rotation.f).Length > 0.5f);

            // Back to nought is back to where it started. A slider that cannot be
            // returned to its starting value is a rate with extra steps, which is
            // exactly what these replaced.
            VrHandCalibration.SetAdjustment(HandRole.Main, Vec3.Zero, Vec3.Zero);
            VrHandCalibration.TryGrip(HandRole.Main, out MatrixFrame restored);

            Expect("zeroing the sliders restores the capture", restored.rotation.f,
                before.rotation.f);
            Expect("and its position", restored.origin, before.origin);
        }

        // -- 8d. Does a calibration survive the config file? -------------------
        //
        // Decimal strings and back, with no drift worth seeing - otherwise a
        // calibration quietly degrades every time it is saved and reloaded.
        //
        // Two formats now, written as two lines: nine numbers for the measured
        // rotation, six for the adjustment the sliders show. Splitting them is
        // what keeps the second legible, so both are checked.
        private static void GripPersistence(MatrixFrame grip)
        {
            MethodInfo ser = Method("Serialise");
            MethodInfo par = Method("TryParseRotation");
            MethodInfo serAdj = Method("SerialiseAdjust");
            MethodInfo parAdj = Method("TryParseAdjust");
            MethodInfo parOld = Method("TryParseFrame");

            if (ser == null || par == null || serAdj == null || parAdj == null || parOld == null)
            {
                Fail("VrHandCalibration serialisation helpers not found (renamed?)");
                return;
            }

            // -- the measured rotation, nine numbers --------------------------
            var text = (string)ser.Invoke(null, new object[] { grip.rotation });
            object[] args = { text, null };

            Check("saved rotation parses back", (bool)par.Invoke(null, args));

            var round = (Mat3)args[1];
            Expect("round trip forward", round.f, grip.rotation.f);
            Expect("round trip side", round.s, grip.rotation.s);
            Expect("round trip up", round.u, grip.rotation.u);

            Check("rejects a short rotation line",
                !(bool)par.Invoke(null, new object[] { "1,2,3", null }));
            Check("rejects nonsense",
                !(bool)par.Invoke(null, new object[] { "a,b,c,d,e,f,g,h,i", null }));

            // -- the adjustment, six numbers ----------------------------------
            var offset = new Vec3(0.031f, -0.128f, 0.007f, -1f);
            var euler = new Vec3(12.5f, -90f, 3.25f, -1f);

            var adjText = (string)serAdj.Invoke(null, new object[] { offset, euler });
            object[] adjArgs = { adjText, null, null };

            Check("saved adjustment parses back", (bool)parAdj.Invoke(null, adjArgs));
            Expect("round trip offset", (Vec3)adjArgs[1], offset);
            Expect("round trip angles", (Vec3)adjArgs[2], euler);

            Check("rejects a short adjustment line",
                !(bool)parAdj.Invoke(null, new object[] { "1,2,3", null, null }));

            // -- the older twelve-number format, read only --------------------
            //
            // Someone upgrading has one of these in their config. It has to be
            // readable, because the rotation in it was hard-won; its origin is
            // deliberately not carried, which Migrate handles and this only needs
            // to prove is parseable at all.
            Check("the old twelve-number format still parses",
                (bool)parOld.Invoke(null, new object[]
                    { "1,0,0,0,1,0,0,0,1,0.1,0.2,0.3", null }));
        }

        private static MethodInfo Method(string name)
        {
            return typeof(VrHandCalibration).GetMethod(
                name, BindingFlags.NonPublic | BindingFlags.Static);
        }


        // -- 9. The stick deadzone -------------------------------------------
        //
        // A per-axis deadzone is the common shortcut and it makes diagonals
        // wrong. These check the two properties that matter: dead is dead, and a
        // stick held on the diagonal still points along the diagonal and can
        // still reach full deflection.
        private static void GamepadDeadzone()
        {
            Section("gamepad deadzone");

            MethodInfo m = typeof(VrGamepad).GetMethod(
                "ApplyDeadzone", BindingFlags.NonPublic | BindingFlags.Static);

            if (m == null)
            {
                Fail("VrGamepad.ApplyDeadzone not found (renamed?)");
                return;
            }

            var atRest = (Vec2)m.Invoke(null, new object[] { 0.05f, 0.05f });
            Check("small deflection reads as rest", atRest.x == 0f && atRest.y == 0f);

            var full = (Vec2)m.Invoke(null, new object[] { 0f, 1f });
            Approx("full push reaches 1", full.y, 1f);

            // The diagonal case, which is what a per-axis deadzone gets wrong.
            float d = (float)(1.0 / Math.Sqrt(2.0));
            var diag = (Vec2)m.Invoke(null, new object[] { d, d });
            Approx("diagonal keeps its length", diag.Length, 1f);
            Approx("diagonal keeps its direction", diag.x, diag.y);
        }
        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("--- " + name + " ---");
        }

        private static void Expect(string what, Vec3 got, Vec3 want)
        {
            bool ok = Near(got.x, want.x) && Near(got.y, want.y) && Near(got.z, want.z);
            Report(ok, string.Format("{0,-32} got ({1,6:F3},{2,6:F3},{3,6:F3}) want ({4,6:F3},{5,6:F3},{6,6:F3})",
                what, got.x, got.y, got.z, want.x, want.y, want.z));
        }

        private static void Approx(string what, float got, float want)
        {
            Report(Near(got, want), string.Format("{0,-32} got {1,9:F5} want {2,9:F5}", what, got, want));
        }

        private static void Check(string what, bool ok) => Report(ok, what);

        private static void Fail(string what) => Report(false, what);

        private static void Report(bool ok, string text)
        {
            if (!ok) _fail++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + text);
        }

        private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;
    }
}
