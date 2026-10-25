using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using Path = System.IO.Path;
using System.Runtime.InteropServices;
using BannerlordVR.Interop;
using BannerlordVR.VR;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);

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
            DlssRuntimeUpgrade();
            VoiceCacheRecovery();
            BattleGripAndVisuals();
            BowStringSelection();
            GripCalibration();
            GamepadDeadzone();
            ArmIkGeometry();
            ArmBendConstraints();
            ArmBendSmoothing();
            ArmTwist();
            ArcheryArmsAndBodyTurn();
            LegIkGeometry();
            RoomscaleControl();
            ScaledFrameRoundTrip();
            MeleeBlockGeometry();
            MotionMeleeGestures();
            MeleeSweepAndHaptics();
            PhysicalMeleeRework();
            EnemyBladeFit();
            PatchParameterNames();
            ManualArchery();
            Holstering();
            GlideAndPickup();
            WeaponWeight();
            SolidWeapon();
            HeadDrivenBody();
            SmoothBody();
            BodyWeightArmLength();
            ThrowAndCrossbow();
            CrossbowCalibration();
            RidingAndHelm();
            ReinAppearance();
            FingerRig();
            SiegeAndLadder();
            UiSplit();
            DeploymentCommanderHit();

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? "ALL CHECKS PASSED" : _fail + " CHECK(S) FAILED");
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        private sealed class VoiceLookup : System.Runtime.Remoting.Proxies.RealProxy
        {
            internal bool Ready;
            internal int Calls;
            internal readonly Dictionary<string, int> Codes = new Dictionary<string, int>();

            internal VoiceLookup(Type contract) : base(contract) { }

            public override System.Runtime.Remoting.Messaging.IMessage Invoke(System.Runtime.Remoting.Messaging.IMessage message)
            {
                var call = (System.Runtime.Remoting.Messaging.IMethodCallMessage)message;
                if (call.MethodName != "GetVoiceTypeIndex")
                    throw new NotSupportedException(call.MethodName);
                Calls++;
                string name = (string)call.Args[0];
                if (Ready && !Codes.ContainsKey(name))
                    Codes.Add(name, Codes.Count);
                return new System.Runtime.Remoting.Messaging.ReturnMessage(
                    Ready ? Codes[name] : -1, null, 0, call.LogicalCallContext, call);
            }
        }

        private static void VoiceCacheRecovery()
        {
            Section("voice IDs cached before native voice definitions load");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var native = typeof(MBAPI).GetField("IMBVoiceManager", flags);
            object original = native.GetValue(null);
            var lookup = new VoiceLookup(native.FieldType);
            native.SetValue(null, lookup.GetTransparentProxy());
            try
            {
                Type voices = typeof(SkinVoiceManager).GetNestedType("VoiceType");
                FieldInfo charge = voices.GetField("Charge");
                Func<int> index = () => ((SkinVoiceManager.SkinVoiceType)charge.GetValue(null)).Index;
                Check("early initialization reproduces the silent commander", index() == -1);
                var repair = typeof(SubModule).GetMethod("RepairStaleVoiceCodes", flags);
                repair.Invoke(null, null);
                Check("unloaded voice definitions are left alone", index() == -1);
                lookup.Ready = true;
                repair.Invoke(null, null);
                Check("Charge resolves after voice definitions load", index() == lookup.Codes["Charge"]);
                foreach (FieldInfo field in voices.GetFields(BindingFlags.Static | BindingFlags.Public))
                {
                    if (field.FieldType != typeof(SkinVoiceManager.SkinVoiceType))
                        continue;
                    var voice = (SkinVoiceManager.SkinVoiceType)field.GetValue(null);
                    Check(field.Name + " also resolves", voice.Index == lookup.Codes[voice.TypeID]);
                }
                foreach (var voice in (SkinVoiceManager.SkinVoiceType[])voices.GetField("MpBarks").GetValue(null))
                    Check(voice.TypeID + " bark also resolves", voice.Index == lookup.Codes[voice.TypeID]);
                int calls = lookup.Calls;
                repair.Invoke(null, null);
                Check("a healthy cache is not rebuilt", lookup.Calls == calls + 1);
            }
            finally { native.SetValue(null, original); }
        }

        private static void DlssRuntimeUpgrade()
        {
            Section("bundled DLSS upgrade and original backup");
            var upgrade = typeof(VrCameraDriver).Assembly.GetType("BannerlordVR.VR.VrDlssRuntime", true)
                .GetMethod("Upgrade", BindingFlags.Static | BindingFlags.NonPublic);
            string bundled = Path.Combine(Path.GetDirectoryName(typeof(VrCameraDriver).Assembly.Location), "nvngx_dlss.dll");
            string root = Path.Combine(Path.GetTempPath(), "bvr-dlss-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string installed = Path.Combine(root, "nvngx_dlss.dll");
            string backup = installed + ".bannerlordvr-original.bak";
            Func<string, bool> run = source => (bool)upgrade.Invoke(null, new object[] { source, installed });
            try
            {
                File.Copy(typeof(VrCameraDriver).Assembly.Location, installed);
                byte[] original = File.ReadAllBytes(installed);
                Check("an older runtime is upgraded", run(bundled));
                Check("the original is backed up byte for byte", Convert.ToBase64String(File.ReadAllBytes(backup)) == Convert.ToBase64String(original));
                Check("installed runtime matches the bundle", new FileInfo(installed).Length == new FileInfo(bundled).Length
                    && FileVersionInfo.GetVersionInfo(installed).FileVersion == FileVersionInfo.GetVersionInfo(bundled).FileVersion);
                Check("an equal runtime is left alone", !run(bundled));
                var name = new AssemblyName("FutureDlss") { Version = new Version(999, 0, 0, 0) };
                var future = AppDomain.CurrentDomain.DefineDynamicAssembly(name, System.Reflection.Emit.AssemblyBuilderAccess.Save, root);
                future.DefineDynamicModule("FutureDlss", "future.dll");
                future.DefineVersionInfoResource();
                future.Save("future.dll");
                File.Copy(Path.Combine(root, "future.dll"), installed, true);
                Check("a newer runtime is never downgraded", !run(bundled));
                File.Copy(typeof(VrCameraDriver).Assembly.Location, installed, true);
                File.WriteAllText(backup, "previous backup");
                Check("a restored old runtime is upgraded again", run(bundled));
                Check("an existing backup is preserved", File.ReadAllText(backup) == "previous backup");
                Check("a missing bundle leaves the runtime alone", !run(Path.Combine(root, "missing.dll")));
                string corrupt = Path.Combine(root, "corrupt.dll");
                File.WriteAllText(corrupt, "not an NVIDIA runtime");
                bool rejected = false;
                try { run(corrupt); } catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidDataException; }
                Check("a corrupt or substituted bundle is rejected", rejected);
                File.Copy(typeof(VrCameraDriver).Assembly.Location, installed, true);
                using (File.Open(installed, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    rejected = false;
                    try { run(bundled); } catch (TargetInvocationException) { rejected = true; }
                    Check("a locked target fails without overwriting it", rejected);
                }
                Check("a failed upgrade preserves the target", Convert.ToBase64String(File.ReadAllBytes(installed)) == Convert.ToBase64String(original));
                Check("a failed upgrade leaves no temporary files", Directory.GetFiles(root, "*.tmp").Length == 0);
                File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), installed, true);
                IntPtr loaded = LoadLibrary(installed);
                Check("the loaded-DLL fixture loads", loaded != IntPtr.Zero);
                try { Check("an already loaded old DLL can be upgraded on disk", run(bundled)); }
                finally { if (loaded != IntPtr.Zero) FreeLibrary(loaded); }
                File.Delete(installed);
                Check("a missing installed runtime is installed", run(bundled));
            }
            finally { Directory.Delete(root, true); }
        }

        private static void BattleGripAndVisuals()
        {
            Section("battle grip toggle, bowstring endpoints and mounted travel");
            Assembly assembly = typeof(VrCameraDriver).Assembly;
            Type latchType = assembly.GetType("BannerlordVR.VR.VrGrip+Latch", true);
            object latch = Activator.CreateInstance(latchType, true);
            MethodInfo sample = latchType.GetMethod("Sample", BindingFlags.Instance | BindingFlags.NonPublic);
            Func<float, bool, bool, bool, bool, float> read = (value, tracked, toggle, ready, owned) =>
                (float)sample.Invoke(latch, new object[] { value, tracked, toggle, ready, owned });
            Check("toggle starts open", read(0, true, true, true, false) == 0);
            Check("first press takes hold", read(1, true, true, true, false) == 1);
            Check("repeated readers do not retoggle", read(1, true, true, true, true) == 1);
            Check("release keeps an acquired hold", read(0, true, true, true, true) == 1);
            Check("second press releases", read(1, true, true, true, true) == 0);
            Check("release press cannot also become a vanilla attack",
                (bool)latchType.GetProperty("ReleasedByPress", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(latch));
            read(0, true, true, true, false);
            read(1, true, true, true, false);
            Check("a missed grab clears on release", read(0, true, true, true, false) == 0);
            read(1, true, true, true, false);
            read(0, true, true, true, true);
            Check("menu release preserves hold", read(0, true, true, false, true) == 1);
            Check("a menu press does not release", read(1, true, true, false, true) == 1);
            Check("returning with grip down creates no edge", read(1, true, true, true, true) == 1);
            read(0, true, true, true, true);
            read(0, false, true, true, true);
            Check("tracking recovery creates no edge", read(1, true, true, true, true) == 1);
            read(0, true, true, true, true);
            Check("switching to hold does not drop immediately", read(0, true, false, true, true) == 1);
            read(1, true, false, true, true);
            Check("hold mode releases after a physical regrip", read(0, true, false, true, true) == 0);
            Check("physical bowstring value passes through", read(0.35f, true, false, true, false) == 0.35f);

            Type bowType = assembly.GetType("BannerlordVR.VR.VrDrawnBow", true);
            MethodInfo cord = bowType.GetMethod("CordFrame", BindingFlags.Static | BindingFlags.NonPublic);
            Vec3 tip = new Vec3(2, 3, 4);
            foreach (Vec3 pull in new[] { new Vec3(2, 3, 5), new Vec3(2.4f, 2.5f, 4.3f), new Vec3(2, 3, 3) })
            {
                MatrixFrame frame = (MatrixFrame)cord.Invoke(null, new object[] { tip, pull, MatrixFrame.Identity, 1f, 1f });
                Check("cord starts at limb and ends at hand, including vertical draws",
                    (frame.TransformToParent(Vec3.Zero) - tip).Length < 1e-5f
                    && (frame.TransformToParent(new Vec3(0, 1, 0)) - pull).Length < 1e-5f);
            }
            Vec3 captured = new Vec3(10, 20, 0), eye = new Vec3(10.2f, 20, 2.5f);

            MethodInfo saddleTravel = typeof(VrCameraDriver).GetMethod("SaddleAnimationTravel",
                BindingFlags.Static | BindingFlags.NonPublic);
            Vec3 roomOffset = new Vec3(0.08f, -0.1f, 0.12f);
            Vec3 previousRoot = captured, previousEye = eye;
            foreach (Vec3 gait in new[] { Vec3.Zero, new Vec3(0, 0.3f, 0.08f), new Vec3(0, -0.12f, -0.04f), Vec3.Zero })
            {
                Vec3 root = previousRoot + new Vec3(0, 0.15f, 0);
                Vec3 currentEye = root + new Vec3(0.2f, 0, 2.5f) + gait;
                Vec3 travel = root - previousRoot, guessedLead = new Vec3(0, 0.18f, 0);
                Vec3 animation = (Vec3)saddleTravel.Invoke(null, new object[] { previousEye, currentEye, travel });
                Vec3 finished = previousEye + roomOffset + guessedLead + (travel - guessedLead) + animation;
                Check("camel gait and root travel finish together without doubling travel or changing room offset",
                    (finished - currentEye - roomOffset).Length < 1e-5f);
                previousRoot = root;
                previousEye = currentEye;
            }
            Check("a stopped camel adds no continuing drift",
                ((Vec3)saddleTravel.Invoke(null, new object[] { previousEye, previousEye, Vec3.Zero })).Length < 1e-5f);

            Type headBody = assembly.GetType("BannerlordVR.VR.VrHeadBody", true);
            MethodInfo eyeTarget = headBody.GetMethod("RenderedEyeTarget", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (float slope in new[] { -0.6f, 0f, 0.6f })
            foreach (float scale in new[] { 1f, 1.15f })
            {
                MatrixFrame body = MatrixFrame.Identity;
                body.origin = new Vec3(664f, 889f, 19f);
                body.rotation.RotateAboutUp(0.7f);
                body.rotation.RotateAboutSide(slope);
                body.rotation.s *= scale;
                body.rotation.f *= scale;
                body.rotation.u *= scale;
                Vec3 saddle = body.TransformToParent(new Vec3(0f, 0.1f, 2.5f));
                Vec3 renderedEye = saddle + roomOffset;
                // The body can advance/pitch again after the camera is prepared.
                body.origin += new Vec3(0.1f, 0.2f, 0.08f);
                body.rotation.RotateAboutSide(0.04f);
                Vec3 target = (Vec3)eyeTarget.Invoke(null, new object[] { body, renderedEye });
                Check("camel body targets the rendered eye uphill/downhill with scale and a later pose",
                    (body.TransformToParent(target) - renderedEye).Length < 0.0002f);
            }
        }

        private static void BowStringSelection()
        {
            Section("sparse bowstring beside dense recurve tips");
            Type bow = typeof(VrCameraDriver).Assembly.GetType("BannerlordVR.VR.VrDrawnBow", true);
            MethodInfo fit = bow.GetMethod("FindStringRings", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (float tilt in new[] { 0f, 0.02f })
            {
                var points = new List<Vec3>();
                // Noble bow's measured string: three four-sided rings, 1.466 m long.
                foreach (float z in new[] { -0.73086f, 0f, 0.73493f })
                    foreach (Vec3 radial in new[] { new Vec3(0.00238f, 0, 0), new Vec3(-0.00238f, 0, 0),
                        new Vec3(0, 0.00238f, 0), new Vec3(0, -0.00238f, 0) })
                        points.Add(new Vec3(0.212945f + tilt * z, 0, z) + radial);
                // More wood vertices than string vertices, on a competing straight line.
                foreach (float z in new[] { -0.54f, -0.52f, -0.5f, 0.5f, 0.52f, 0.54f })
                    for (int i = 0; i < 8; i++)
                    {
                        float angle = i * (float)Math.PI / 4;
                        points.Add(new Vec3(0.176f + 0.003f * (float)Math.Cos(angle),
                            0.003f * (float)Math.Sin(angle), z));
                    }
                var near = new List<int>();
                for (int i = 0; i < points.Count; i++) near.Add(i);
                object[] args = { points.ToArray(), near, 2, -0.75921f, 1.51821f, Vec3.Zero, Vec3.Zero };
                var picked = (List<int>)fit.Invoke(null, args);
                Check("all 12 vanilla string vertices selected; dense tip wood preserved",
                    picked != null && picked.Count == 12 && picked.TrueForAll(i => i < 12));
                Vec3 at = (Vec3)args[5], dir = (Vec3)args[6];
                Vec3 rest = at - dir * (at.z / dir.z);
                Check("bending copy rests on the original string, including a tilted string",
                    (rest - new Vec3(0.212945f, 0, 0)).Length < 1e-5f);
                near.RemoveAll(i => i >= 4 && i < 8);
                Check("tip rings alone cannot be mistaken for a confirmed string", fit.Invoke(null, args) == null);
            }
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

            // TEST 596: RollFree keeps where the head looks and drops its roll.
            BvrQuat Half(float deg, float x, float y, float z)
            {
                float r = (float)(deg * Math.PI / 360.0);
                float s = (float)Math.Sin(r);
                return Quat(x * s, y * s, z * s, (float)Math.Cos(r));
            }
            BvrQuat Mul(BvrQuat a, BvrQuat b) => Quat(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
            Mat3 tilted = VrMath.ToBannerlord(Mul(Mul(Half(30f, 0f, 1f, 0f), Half(-40f, 1f, 0f, 0f)), Half(25f, 0f, 0f, 1f)));
            Mat3 upright = VrMath.ToBannerlord(VrMath.RollFree(Mul(Mul(Half(30f, 0f, 1f, 0f), Half(-40f, 1f, 0f, 0f)), Half(25f, 0f, 0f, 1f))));
            Expect("RollFree keeps forward", upright.f, tilted.f);
            Expect("RollFree side is level", new Vec3(0f, 0f, upright.s.z, -1f), new Vec3(0f, 0f, 0f, -1f));
            Expect("RollFree up points up", new Vec3(0f, 0f, upright.u.z > 0f ? 1f : 0f, -1f), new Vec3(0f, 0f, 1f, -1f));
            Mat3 down = VrMath.ToBannerlord(VrMath.RollFree(Mul(Half(15f, 0f, 1f, 0f), Half(-90f, 1f, 0f, 0f))));
            Expect("RollFree straight down keeps forward", down.f, new Vec3(0f, 0f, -1f, -1f));
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

            var recentre = (MatrixFrame)compute.Invoke(null, new object[] { head, null, null });
            MatrixFrame corrected = recentre.TransformToParent(head);

            // TEST 389: the anchor is the LEVEL head's eye on the same neck pivot
            // (VrCameraDriver.LevelEye), so the same pivot looking level collapses
            // to it - a recentre taken looking 15 deg down is the one taken level.
            MethodInfo levelEyeM = typeof(VrCameraDriver).GetMethod("LevelEye", BindingFlags.NonPublic | BindingFlags.Static);
            Func<MatrixFrame, Vec3> levelEye = f => (Vec3)levelEyeM.Invoke(null, new object[] { f });
            MatrixFrame level = head;
            level.rotation = VrMath.YawOnly(rot);
            level.origin = levelEye(head);
            MatrixFrame levelCorrected = recentre.TransformToParent(level);
            Approx("recentred origin x (level head, same pivot)", levelCorrected.origin.x, 0f);
            Approx("recentred origin y (level head, same pivot)", levelCorrected.origin.y, 0f);
            Approx("recentred origin z (level head, same pivot)", levelCorrected.origin.z, 0f);
            Check("looking 15 deg down sits below the level eye, not on it", corrected.origin.z < -0.01f);

            // TEST 389: an automatic recentre keeps the F10 height.
            var kept = (MatrixFrame)compute.Invoke(null, new object[] { head, 1.5f, null });
            Approx("a kept height puts the level eye at its old height", kept.TransformToParent(level).origin.z,
                   levelEye(head).z - 1.5f);

            // TEST 661: a recentre that keeps an earlier one's yaw re-zeroes the position only.
            MatrixFrame turned = head;
            turned.rotation = Compose(yaw + 0.6f, pitch);
            var posOnly = (MatrixFrame)compute.Invoke(null, new object[] { turned, null, recentre.rotation });
            Approx("a kept yaw keeps the rotation", posOnly.rotation.f.x, recentre.rotation.f.x);
            Approx("a kept yaw keeps the rotation (y)", posOnly.rotation.f.y, recentre.rotation.f.y);
            MatrixFrame turnedLevel = level;
            turnedLevel.origin = levelEye(turned);
            Approx("a kept yaw still zeroes the eye", posOnly.TransformToParent(turnedLevel).origin.Length, 0f);

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
            Approx("1 m forward -> +1 on Y", movedCorrected.origin.y - corrected.origin.y, 1f);
            Approx("1 m forward -> 0 on X", movedCorrected.origin.x - corrected.origin.x, 0f);
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
        // The native side asserts sizeof == 144 at compile time. This is the other
        // half of that pair: the same number, checked against what the marshaller
        // will actually do, plus the offsets, so a field inserted in the middle of
        // one declaration and not the other is caught by a test run rather than by
        // a world that is suddenly the wrong size.
        private static void OverlayAbi()
        {
            Section("overlay ABI layout");

            Type t = typeof(BvrOverlayState);

            Report(Marshal.SizeOf(t) == 788 && Marshal.OffsetOf(t, "WristHudWidth").ToInt32() == 176
                   && Marshal.OffsetOf(t, "Vrs").ToInt32() == 180
                   && Marshal.OffsetOf(t, "VrsEye").ToInt32() == 184
                   && Marshal.OffsetOf(t, "HeadUpMounted").ToInt32() == 188
                   && Marshal.OffsetOf(t, "ArmScale").ToInt32() == 192
                   && Marshal.OffsetOf(t, "BodyWeight").ToInt32() == 196
                   && Marshal.OffsetOf(t, "HudHitMarker").ToInt32() == 200
                   && Marshal.OffsetOf(t, "HudPrompts").ToInt32() == 204
                   && Marshal.OffsetOf(t, "HandFingers").ToInt32() == 208
                   && Marshal.OffsetOf(t, "MapHandUiWidth").ToInt32() == 212
                   && Marshal.OffsetOf(t, "DpadThumbrest").ToInt32() == 216
                   && Marshal.OffsetOf(t, "UiWorldScaleMap").ToInt32() == 224
                   && Marshal.OffsetOf(t, "OwnShieldBash").ToInt32() == 228
                   && Marshal.OffsetOf(t, "UiWorldScaleDeploy").ToInt32() == 232
                   && Marshal.OffsetOf(t, "UiRecenterNewScreen").ToInt32() == 236
                   && Marshal.OffsetOf(t, "UiRecenterAngle").ToInt32() == 240
                   && Marshal.OffsetOf(t, "FovTrueVertical").ToInt32() == 244
                   && Marshal.OffsetOf(t, "ToolMode").ToInt32() == 248
                   && Marshal.OffsetOf(t, "ToolCommand").ToInt32() == 260
                   && Marshal.OffsetOf(t, "ToolValue").ToInt32() == 264
                   && Marshal.OffsetOf(t, "ToolLabel").ToInt32() == 288
                   && Marshal.OffsetOf(t, "ToolStatus").ToInt32() == 416
                   && Marshal.OffsetOf(t, "ToolProfile").ToInt32() == 544
                   && Marshal.OffsetOf(t, "ToolSlot0").ToInt32() == 552
                   && Marshal.OffsetOf(t, "ToolSlot3").ToInt32() == 696
                   && Marshal.OffsetOf(t, "FovCropH").ToInt32() == 744
                   && Marshal.OffsetOf(t, "FovCropV").ToInt32() == 748
                   && Marshal.OffsetOf(t, "FovCropDetailed").ToInt32() == 752
                   && Marshal.OffsetOf(t, "FovCropDown").ToInt32() == 768
                   && Marshal.OffsetOf(t, "SharpenMode").ToInt32() == 772
                   && Marshal.OffsetOf(t, "DlssPreset").ToInt32() == 776
                   && Marshal.OffsetOf(t, "GripToggle").ToInt32() == 780
                   && Marshal.OffsetOf(t, "EnemyPhysics").ToInt32() == 784,
                string.Format(
                "BvrOverlayState is {0} bytes (want 788, EnemyPhysics last at 784; bvr_api.cpp static_asserts it)",
                Marshal.SizeOf(t)));

            // Forty-one four-byte fields in declared order. Named individually
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
                "TableauProbe", "TableauProbeState", "Sharpen", "StickMouseSpeed",
                "ResolutionApply", "ResolutionState", "PhysicalCombat", "BodyIk",
                "MotionMelee", "FriendlyFire", "GripController", "Holster", "MapVr",
                "HeadUp", "HeadForward", "BodyTurnDeg", "Recenter", "HandsOnly",
            };

            for (int i = 0; i < names.Length; i++)
            {
                int want = i * 4;
                int got = Marshal.OffsetOf(t, names[i]).ToInt32();
                Report(got == want, string.Format(
                    "{0,-14} at offset {1,2} (want {2})", names[i], got, want));
            }

            // TEST 377: the wrist regions, passed by reference to native.
            Type w = typeof(BvrWristRegions);
            Report(Marshal.SizeOf(w) == 144, string.Format(
                "BvrWristRegions is {0} bytes (want 144, as in bvr_api.h)", Marshal.SizeOf(w)));
            Report(Marshal.OffsetOf(w, "Rects").ToInt32() == 16, string.Format(
                "BvrWristRegions.Rects at offset {0} (want 16)", Marshal.OffsetOf(w, "Rects").ToInt32()));

            // TEST 378: the laser's hit, read back from native.
            Type l = typeof(BvrLaser);
            Report(Marshal.SizeOf(l) == 24 && Marshal.OffsetOf(l, "U").ToInt32() == 12, string.Format(
                "BvrLaser is {0} bytes with U at {1} (want 24 and 12, as in bvr_api.h)",
                Marshal.SizeOf(l), Marshal.OffsetOf(l, "U").ToInt32()));
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
        /// <summary>
        /// The weapon-vs-weapon line geometry TEST 242 rests on: the engine's
        /// along-blade distance picks the blade direction, and the grip near the
        /// attach bone scores a chord that passes a hand's width away from it.
        /// A wrong direction must not score like a right one, or blocking would
        /// accept any contact near the body again.
        /// </summary>
        private static void MeleeBlockGeometry()
        {
            Section("weapon-vs-weapon line geometry (TEST 242)");

            MethodInfo score = typeof(VrPhysicalCombat).GetMethod(
                "LineScore", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo closest = typeof(VrPhysicalCombat).GetMethod(
                "ClosestOnSegment", BindingFlags.NonPublic | BindingFlags.Static);

            if (score == null || closest == null)
            {
                Fail("VrPhysicalCombat block geometry not found via reflection");
                return;
            }

            // A blade along +Y: grip at the origin, tip at 1 m. The engine says
            // the contact sits 0.6 m along it.
            var grip = new Vec3(0f, 0f, 0f, -1f);
            var tip = new Vec3(0f, 1f, 0f, -1f);
            var axis = new Vec3(0f, 1f, 0f, -1f);
            var contact = new Vec3(0f, 0.6f, 0f, -1f);

            float trueScore = (float)score.Invoke(null, new object[] { grip, contact, axis, 0.6f });
            Check("true axis scores ~0", trueScore < 1e-4f);

            var wrong = new Vec3(1f, 0f, 0f, -1f);
            float wrongScore = (float)score.Invoke(null, new object[] { grip, contact, wrong, 0.6f });
            Check("perpendicular axis scores big", wrongScore > 0.5f);

            // The grip sits a hand's width off the attach bone, which is the
            // real case: the chord is still the blade and must land inside the
            // line slack, not be thrown out as a wrong direction.
            var bone = new Vec3(0f, -0.1f, 0f, -1f);
            float offset = (float)score.Invoke(null, new object[] { bone, contact, axis, 0.6f });
            Check("grip offset still validates", offset < 0.25f);
            Approx("grip offset score", offset, 0.05f);

            var anywhere = new Vec3(0.3f, 0.25f, 0.7f, -1f);
            var onSegment = (Vec3)closest.Invoke(null, new object[] { anywhere, grip, tip });
            Expect("closest point on the blade", onSegment, new Vec3(0f, 0.25f, 0f, -1f));

            // TEST 295: a frame's sweep is cut finely enough that a charging
            // body cannot pass between two blade poses.
            MethodInfo cuts = typeof(VrPhysicalCombat).GetMethod(
                "SweepCuts", BindingFlags.NonPublic | BindingFlags.Static);
            if (cuts == null)
            {
                Fail("VrPhysicalCombat.SweepCuts not found via reflection");
                return;
            }

            Func<int, float, float, int> cut = (min, travel, dt) =>
                (int)cuts.Invoke(null, new object[] { min, travel, dt });

            Check("a still blade keeps the configured minimum", cut(1, 0f, 0.011f) == 1);
            // 90 fps, tip 0.17 m + a gallop's 0.13 m = 0.30 m -> poses <= 0.1 m apart.
            int fast = cut(1, 0.17f, 0.011f);
            Check("a fast swing at 90 fps is cut to <= 0.1 m", 0.30f / (fast + 1) <= 0.1001f);
            // 45 fps (reprojection) doubles it.
            int slow = cut(1, 0.34f, 0.022f);
            Check("a reprojected frame is cut to <= 0.1 m", 0.61f / (slow + 1) <= 0.1001f);
            Check("a hitch is capped at 8", cut(1, 5f, 0.1f) == 8);
        }

        // -- 10. Motion melee gestures (TEST 245) ------------------------------
        //
        // The gesture table decides which way the character swings and which
        // attack it raises a guard against, and every part of it is pure
        // arithmetic. A mirrored axis here is a sword that swings the wrong way;
        // a "vertical" read as a "forward" is a stab where an overhead was
        // asked for.
        //
        // The guard thresholds come from the live config, so these assert
        // positions that are unambiguous under any sane value - a hand a metre
        // out to a side, a hand a metre above the head - rather than the exact
        // millimetres the keys are tuned to.
        private static void MotionMeleeGestures()
        {
            Section("motion melee gestures (TEST 245)");

            MethodInfo gesture = typeof(VrMelee).GetMethod(
                "Gesture", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo guardFor = typeof(VrMelee).GetMethod(
                "GuardFor", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo parse = typeof(VrMelee).GetMethod(
                "ParseMap", BindingFlags.NonPublic | BindingFlags.Static);

            if (gesture == null || guardFor == null || parse == null)
            {
                Fail("VrMelee gesture helpers not found via reflection");
                return;
            }

            // Body axes: x = right, y = forward, z = up.
            var cases = new[]
            {
                new object[] {  1.0f,  0.0f,  0.0f,  1, "hand right" },
                new object[] { -1.0f,  0.0f,  0.0f,  0, "hand left" },
                new object[] {  0.0f,  0.0f,  1.0f,  2, "hand up" },
                new object[] {  0.0f,  0.0f, -1.0f,  2, "hand down (still the overhead)" },
                new object[] {  0.0f,  1.0f,  0.0f,  3, "hand forward (stab)" },
                new object[] {  0.0f, -1.0f,  0.0f, -1, "hand pulled back is not an attack" },
                new object[] {  0.4f,  1.0f,  0.0f,  3, "forward beats a diagonal side pull" },
                new object[] {  0.4f,  0.0f,  1.0f,  2, "vertical beats a diagonal side pull" },
            };

            foreach (object[] c in cases)
            {
                int got = (int)gesture.Invoke(null, new object[]
                    { new Vec3((float)c[0], (float)c[1], (float)c[2], -1f) });

                Report(got == (int)c[3], string.Format(
                    "{0,-40} -> {1} (want {2})", (string)c[4], got, (int)c[3]));
            }

            // Where the hand is decides the guard: up, a side, or low middle.
            //
            // Compared as NUMBERS, not names, and that is not pedantry:
            // UsageDirection has aliases - AttackEnd is 4 and DefendUp is 4,
            // DefendBegin is 4 as well - and ToString() on this runtime answers
            // "DefendBegin" for the guard the whole feature exists to write.
            // The values are the contract (Usage_direction in the game data:
            // up 4, down 5, left 6, right 7); the names are not.
            var guards = new[]
            {
                new object[] {  0.0f, 0.3f,  1.0f, 4, "hand centred and high -> guard up" },
                new object[] {  1.0f, 0.3f,  0.0f, 7, "hand out to the right -> guard right" },
                new object[] { -1.0f, 0.3f,  0.0f, 6, "hand out to the left -> guard left" },
                new object[] {  0.0f, 0.3f, -1.0f, 5, "hand low and centred -> the stab guard" },
                new object[] {  0.3f, 0.3f,  0.05f, 7, "side beats height: right at head height is still right" },
                new object[] {  0.05f, 0.3f, -0.2f, 5, "centred at the chest is the stab guard" },
            };

            foreach (object[] c in guards)
            {
                object dir = guardFor.Invoke(null, new object[]
                    { new Vec3((float)c[0], (float)c[1], (float)c[2], -1f) });

                int got = Convert.ToInt32(dir);
                Report(got == (int)c[3], string.Format(
                    "{0,-48} -> {1} (want {2})", (string)c[4], got, (int)c[3]));
            }

            // The shipped default: hand left/right/vertical/forward asks for
            // AttackLeft/Right/Up/Down. Up is the engine's overswing (the
            // overhead) and down is its thrust - the pairing that looks wrong
            // written down and is right in item_usage_sets.xml.
            var map = (Array)parse.Invoke(null, new object[] { "left,right,up,down" });
            string[] want = { "AttackLeft", "AttackRight", "AttackUp", "AttackDown" };

            for (int i = 0; i < want.Length && i < map.Length; i++)
            {
                string got = map.GetValue(i).ToString();
                Report(got == want[i], string.Format(
                    "melee_swing_map entry {0} -> {1} (want {2})", i + 1, got, want[i]));
            }

            // TEST 261: the guard DIRECTION the engine's own auto-block model
            // answers in is an ATTACK direction, and the engine's controller
            // crosses left/right when it turns one into defend flags (the
            // decompiled mapping: an attack from your left is guarded with
            // DefendRight). Reading the answer straight was the bug this
            // checks against, and the flag values are the contract.
            MethodInfo guardFlag = typeof(VrMelee).GetMethod(
                "GuardFlag", BindingFlags.NonPublic | BindingFlags.Static);

            if (guardFlag == null)
            {
                Fail("VrMelee.GuardFlag not found via reflection");
                return;
            }

            var flagCases = new[]
            {
                new object[] { "AttackUp", 0x1000, "an overhead guards up" },
                new object[] { "AttackDown", 0x2000, "a thrust guards down" },
                new object[] { "AttackLeft", 0x800, "an attack from the left guards right" },
                new object[] { "AttackRight", 0x400, "an attack from the right guards left" },
                new object[] { "DefendUp", 0x1000, "the hand guard stays as written (up)" },
                new object[] { "DefendLeft", 0x400, "the hand guard stays as written (left)" },
            };

            foreach (object[] c in flagCases)
            {
                object dir = Enum.Parse(typeof(Agent.UsageDirection), (string)c[0]);
                int got = Convert.ToInt32(guardFlag.Invoke(null, new object[] { dir }));
                Report(got == (int)c[1], string.Format(
                    "{0,-44} -> 0x{1:X4} (want 0x{2:X4})", (string)c[2], got, (int)c[1]));
            }
        }

        // -- 12. The swept blade and the hit pulse (TEST 261) ------------------
        //
        // The in-between sweep exists because a rotational swing carries a
        // body through the fan between the blade's two end poses. The case
        // asserted here is exactly that: a 90-degree swing where a body sits
        // at 135 degrees, 0.6 m from the pivot - both end blades miss it by
        // 0.42 m, and only the in-between blade passes through it. The same
        // check catches the naive fix: a plain lerp of the two tips lands at
        // 0.64 of the blade's reach, short of the body, so the interpolation
        // must re-extend the direction to full length.
        //
        // The hit pulse is checked as a mapping, not against the tuned
        // numbers: monotone in the damage, never past full, and always at
        // least a tick - so a config with other keys still passes.
        private static void MeleeSweepAndHaptics()
        {
            Section("swept blade and hit pulse (TEST 261)");

            MethodInfo segment = typeof(VrPhysicalCombat).GetMethod(
                "SegmentDistance", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(Vec3), typeof(Vec3), typeof(Vec3), typeof(Vec3) }, null);
            MethodInfo sweepTip = typeof(VrPhysicalCombat).GetMethod(
                "SweepTip", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo scale = typeof(VrPhysicalCombat).GetMethod(
                "HitScale", BindingFlags.NonPublic | BindingFlags.Static);

            if (segment == null || sweepTip == null || scale == null)
            {
                Fail("VrPhysicalCombat sweep helpers not found via reflection");
                return;
            }

            // A 90-degree swing about a fixed grip, from -X to +Y.
            var grip = new Vec3(0f, 0f, 0.9f, -1f);
            var prevTip = new Vec3(-0.9f, 0f, 0.9f, -1f);
            var nowTip = new Vec3(0f, 0.9f, 0.9f, -1f);

            // The body stands at 135 degrees, 0.6 m out.
            Vec3 bodyXy = new Vec3(-0.424f, 0.424f, 0f, -1f);
            var feet = new Vec3(bodyXy.x, bodyXy.y, 0f, -1f);
            var head = new Vec3(bodyXy.x, bodyXy.y, 1.7f, -1f);

            var mid = (Vec3)sweepTip.Invoke(null, new object[] { grip, prevTip, grip, nowTip, 0.5f });

            float reach = (mid - grip).Length;
            Approx("the in-between blade keeps full reach", reach, 0.9f);

            float onNow = (float)segment.Invoke(null, new object[] { grip, nowTip, feet, head });
            float onPrev = (float)segment.Invoke(null, new object[] { grip, prevTip, feet, head });
            float onMid = (float)segment.Invoke(null, new object[] { grip, mid, feet, head });

            Approx("the final blade passes 0.42 m away", onNow, 0.424f);
            Approx("the previous blade passes 0.42 m away", onPrev, 0.424f);
            Check("the in-between is the close one", onMid < onNow * 0.1f);

            // The exact midpoint of a 90-degree swing: the bisector at full
            // length. A plain tip lerp would land at (-0.45, 0.45), 0.64 long.
            Approx("the bisector direction", (float)Math.Abs(mid.x), 0.6364f);
            Approx("the bisector direction (y)", mid.y, 0.6364f);

            // Past the tip the distance is to the tip, not to the infinite line.
            var beyond = new Vec3(0f, 2f, 0.9f, -1f);
            float clamped = (float)segment.Invoke(null, new object[] { grip, nowTip, beyond, beyond });
            Approx("beyond the tip measures to the tip", clamped, 1.1f);

            float graze = (float)scale.Invoke(null, new object[] { 0 });
            float half = (float)scale.Invoke(null, new object[] { 20 });
            float full = (float)scale.Invoke(null, new object[] { 40 });
            float huge = (float)scale.Invoke(null, new object[] { 400 });

            Check("the pulse grows with the damage", graze <= half && half <= full);
            Check("a graze still pulses", graze > 0f);
            Check("a graze is lighter than a solid hit", graze < full);
            Approx("40 damage is the full pulse", full, 1f);
            Approx("a huge hit cannot exceed full", huge, 1f);
        }

        // -- TEST 262: the enemy-blade least-squares fit ------------------------
        //
        // v ~= o + u*d, fit from the running sums alone (VrBlade.SolveLine), is
        // the whole of TryGetEnemyBlade's math with the Agent/bone/weapon
        // plumbing stripped away - so it is checked here directly, against an
        // exact synthetic line, rather than through a live mission.
        private static void EnemyBladeFit()
        {
            Section("enemy blade fit (TEST 262)");

            MethodInfo solve = typeof(VrBlade).GetMethod(
                "SolveLine", BindingFlags.NonPublic | BindingFlags.Static);

            if (solve == null)
            {
                Fail("VrBlade.SolveLine not found via reflection");
                return;
            }

            var trueO = new Vec3(0.10f, 0.02f, -0.05f, -1f);
            var trueU = new Vec3(0.90f, 0.10f, 0.05f, -1f);

            int count = 0;
            double sumD = 0.0, sumDD = 0.0, sumVV = 0.0;
            Vec3 sumV = Vec3.Zero;
            Vec3 sumDV = Vec3.Zero;

            // Every real weapon class gets far more than six samples in a real
            // fight; six spread over the weapon's length is enough to prove the
            // math recovers an exact line with ~0 residual.
            float[] ds = { 0.05f, 0.25f, 0.45f, 0.65f, 0.85f, 1.05f };
            foreach (float d in ds)
            {
                Vec3 v = trueO + trueU * d;
                count++;
                sumD += d;
                sumDD += (double)d * d;
                sumVV += v.LengthSquared;
                sumV += v;
                sumDV += v * d;
            }

            object[] args = { count, sumD, sumDD, sumVV, sumV, sumDV, null, null, null };
            bool ok = (bool)solve.Invoke(null, args);
            Check("the fit solves from an exact line", ok);

            if (ok)
            {
                Expect("recovered o", (Vec3)args[6], trueO);
                Expect("recovered u", (Vec3)args[7], trueU);
                Approx("residual is ~0 on an exact line", (float)args[8], 0f);
            }

            // Every sample at the same d cannot be separated into a line - the
            // denominator (n*sumDD - sumD^2) is exactly zero.
            object[] degenerate = { 3, 0.3, 0.03, 0.0, Vec3.Zero, Vec3.Zero, null, null, null };
            bool degenerateOk = (bool)solve.Invoke(null, degenerate);
            Check("every sample at the same d refuses to fit", !degenerateOk);
        }

        // -- 11. Harmony patch parameters bind BY NAME -------------------------        //
        // Every prefix parameter must name a parameter of the target method, or
        // Harmony throws "Parameter X not found" at startup, the SubModule logs
        // it once, and that whole patch class is silently out of the build.
        // MeleeBlowPatch spent TEST 242-246 in exactly that state - the
        // combat-log block fix and TEST 246's blow probe with it - because the
        // shipped method names its Blow parameter `b` and the patch called it
        // `blow`.
        //
        // The names are checked here, against the shipped assemblies, so the
        // next rename is caught by a test run instead of by a session that
        // mysteriously stopped hitting.
        // -- TEST 303-306: the always-live blade, two hands, the shield stick ------
        //
        // The pure halves of the rework: the per-tick sweep against one capsule,
        // the re-hit rule, which blow a weapon can deliver, the two-hand turn, and
        // the frame a stuck missile is stored in. Everything else is engine calls.
        private static void PhysicalMeleeRework()
        {
            Section("always-live blade, re-hit, strike choice, two hands, stick (TEST 303-306)");

            MethodInfo soundPeak = typeof(VrCombatSound).GetMethod("SwingPeak", BindingFlags.NonPublic | BindingFlags.Static);
            if (soundPeak == null)
                Fail("VrCombatSound.SwingPeak not found via reflection");
            else
            {
                float Peak(float previous, bool open, BvrHandState hand) =>
                    (float)soundPeak.Invoke(null, new object[] { previous, open, hand });
                var stillHand = new BvrHandState { IsActive = 1 };
                Check("swing sound: a still controller stays quiet despite a stair position jump",
                      Peak(0f, true, stillHand) < VrCombatSound.SwingGate);
                var swinging = new BvrHandState { IsActive = 1, LinearVelocity = new BvrVec3 { X = 4f } };
                float realPeak = Peak(0f, false, swinging);
                Approx("swing sound: a real controller swing reaches the sound gate", realPeak, 4f);
                Approx("swing sound: its peak survives the next slower frame", Peak(realPeak, true, stillHand), realPeak);
                Approx("swing sound: a new stroke forgets the previous swing", Peak(realPeak, false, stillHand), 0f);
                swinging.IsActive = 0;
                Approx("swing sound: an inactive controller's stale velocity is ignored", Peak(0f, true, swinging), 0f);
                stillHand.LinearVelocity = new BvrVec3 { Y = -4f };
                Approx("swing sound: a deliberate vertical swing still counts", Peak(0f, false, stillHand), 4f);
            }

            MethodInfo sweep = typeof(VrPhysicalCombat).GetMethod(
                "SweepContact", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo choose = typeof(VrPhysicalCombat).GetMethod(
                "ChooseThrust", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo aim = typeof(VrArmIk).GetMethod(
                "Aim", BindingFlags.NonPublic | BindingFlags.Static);

            if (sweep == null || choose == null || aim == null)
            {
                Fail("TEST 303-306 helpers not found via reflection");
                return;
            }

            // 1. A 90-degree swing about a fixed grip; a chest capsule (0.17 m
            //    plus 0.07 of blade and pad) standing at 135 degrees, 0.6 m out -
            //    between the two ticks' blades, where neither end pose reaches.
            var grip = new Vec3(0f, 0f, 1.2f, -1f);
            var prevTip = new Vec3(-0.9f, 0f, 1.2f, -1f);
            var nowTip = new Vec3(0f, 0.9f, 1.2f, -1f);
            var hip = new Vec3(-0.42426f, 0.42426f, 0.9f, -1f);
            var neck = new Vec3(-0.42426f, 0.42426f, 1.5f, -1f);

            object[] args = { grip, prevTip, grip, nowTip, 1, Vec3.Zero, hip, neck, 0.24f, 0f, 0f, 0f };
            bool touched = (bool)sweep.Invoke(null, args);
            Check("a body crossed between two ticks is touched", touched);
            Check("... by the in-between pose", touched && (float)args[9] > 0f && (float)args[9] < 1f);
            Approx("... two thirds along the blade", (float)args[10], 0.6667f);

            var farHip = new Vec3(-0.849f, 0.849f, 0.9f, -1f);
            var farNeck = new Vec3(-0.849f, 0.849f, 1.5f, -1f);
            args = new object[] { grip, prevTip, grip, nowTip, 1, Vec3.Zero, farHip, farNeck, 0.24f, 0f, 0f, 0f };
            Check("a body 0.3 m past the tip is not", !(bool)sweep.Invoke(null, args));

            // 2. A blade held still while a body runs through it at 12 m/s in a
            //    30 Hz tick: 0.2 m one side of the blade a tick ago, 0.2 m the
            //    other side now. Only the body-frame shift finds the crossing.
            var still = new Vec3(0.9f, 0f, 1.2f, -1f);
            var runnerLow = new Vec3(0.45f, 0.2f, 0.9f, -1f);
            var runnerHigh = new Vec3(0.45f, 0.2f, 1.5f, -1f);
            var travel = new Vec3(0f, 12f / 30f, 0f, -1f);

            args = new object[] { grip, still, grip, still, 1, travel, runnerLow, runnerHigh, 0.15f, 0f, 0f, 0f };
            Check("a body running through a still blade is touched", (bool)sweep.Invoke(null, args));
            args = new object[] { grip, still, grip, still, 1, Vec3.Zero, runnerLow, runnerHigh, 0.15f, 0f, 0f, 0f };
            Check("... and only because the sweep moves with it", !(bool)sweep.Invoke(null, args));

            // 3. The re-hit rule, in milliseconds.
            var touches = new VrPhysicalCombat.TouchSet();
            const long Rehit = 150L;
            const long Forget = 2000L;

            Check("a slow touch is not a blow", !touches.Touch(9, 1000L, false, Rehit));
            touches.EndTick(1000L, Forget);
            Check("the same body cut fast a tick later is", touches.Touch(9, 1016L, true, Rehit));
            touches.EndTick(1016L, Forget);

            Check("first touch at speed is a blow", touches.Touch(7, 1000L, true, Rehit));
            touches.EndTick(1000L, Forget);
            Check("staying in the body is not another", !touches.Touch(7, 1050L, true, Rehit));
            Check("another body is its own blow", touches.Touch(8, 1050L, true, Rehit));
            touches.EndTick(1050L, Forget);

            touches.EndTick(1200L, Forget);
            Check("back in 30 ms after leaving is the same contact", !touches.Touch(7, 1230L, true, Rehit));
            touches.EndTick(1230L, Forget);
            touches.EndTick(1300L, Forget);
            Check("out 100 ms and struck again: a new blow", touches.Touch(7, 1400L, true, Rehit));
            touches.EndTick(1400L, Forget);

            touches.EndTick(1420L, Forget);
            Check("back 100 ms after its last blow is held off", !touches.Touch(7, 1500L, true, Rehit));
            touches.EndTick(1500L, Forget);
            touches.EndTick(1600L, Forget);
            Check("a flurry: out again and past 150 ms lands", touches.Touch(7, 1700L, true, Rehit));

            // 4. Which blow the weapon can deliver (TEST 306).
            bool Thrusts(float dot, int swing, DamageTypes swingType, int thrust, DamageTypes thrustType)
                => (bool)choose.Invoke(null, new object[] { dot, swing, swingType, thrust, thrustType, 0.7f });

            Check("a thrust-only spear swung sideways thrusts",
                  Thrusts(0.1f, 0, DamageTypes.Invalid, 90, DamageTypes.Pierce));
            Check("a spear with a swing type but no swing damage thrusts",
                  Thrusts(0f, 0, DamageTypes.Cut, 90, DamageTypes.Pierce));
            Check("a swing-only axe pushed point-first swings",
                  !Thrusts(0.95f, 80, DamageTypes.Cut, 0, DamageTypes.Invalid));
            Check("a sword stabbed thrusts", Thrusts(0.9f, 70, DamageTypes.Cut, 30, DamageTypes.Pierce));
            Check("a sword slashed swings", !Thrusts(0.2f, 70, DamageTypes.Cut, 30, DamageTypes.Pierce));

            // 5. The two-hand turn (TEST 304): the latched hand-space blade axis
            //    lands on the off hand's direction, by the shortest arc - no roll.
            Mat3 hand = Mat3.Identity;
            hand.RotateAboutUp(0.7f);
            hand.RotateAboutSide(0.3f);
            hand.RotateAboutForward(0.5f);

            Vec3 axisInHand = new Vec3(0.1f, 0.95f, 0.3f, -1f).NormalizedCopy();
            Vec3 want = new Vec3(0.2f, 0.4f, 0.9f, -1f).NormalizedCopy();
            var turned = (Mat3)aim.Invoke(null, new object[] { hand, axisInHand, want });

            Expect("the blade points through the off hand", turned.TransformToParent(axisInHand), want);

            Vec3 hinge = Vec3.CrossProduct(hand.TransformToParent(axisInHand), want).NormalizedCopy();
            Expect("the hinge is unmoved (no roll)", turned.TransformToParent(hand.TransformToLocal(hinge)), hinge);

            // 5b. TEST 304c: the SHAFT, 5 cm off the fist, ends up through the off
            //     hand - not merely a line from the fist pointing at it.
            MethodInfo steer = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrTwoHand")?.GetMethod("Steer", BindingFlags.NonPublic | BindingFlags.Static);
            if (steer == null)
            {
                Fail("VrTwoHand.Steer not found via reflection");
            }
            else
            {
                var fist = new Vec3(0.3f, 0.2f, 1.1f, -1f);
                var shaftInHand = new Vec3(0.05f, 0f, 0f, -1f);
                var axisLocal = new Vec3(0f, 1f, 0f, -1f);
                var offHand = new Vec3(0.1f, 0.6f, 1.3f, -1f);
                object[] steerArgs = { hand, fist, shaftInHand, axisLocal, offHand, 1f, false };
                var steered = (Mat3)steer.Invoke(null, steerArgs);

                Vec3 shaftAt = fist + steered.TransformToParent(shaftInHand);
                Vec3 shaftDir = steered.TransformToParent(axisLocal).NormalizedCopy();
                Vec3 toOff = offHand - shaftAt;
                float miss = (toOff - shaftDir * Vec3.DotProduct(toOff, shaftDir)).Length;
                Check("the shaft runs through the off palm (< 5 mm)", (bool)steerArgs[6] && miss < 0.005f);
            }

            // 6. The shield stick (TEST 305): a missile put into the copy's mesh
            //    frame and drawn back as entity x (pose x local) lands where it
            //    started - on a scaled agent too.
            Mat3 rot = Mat3.Identity;
            rot.RotateAboutUp(1.1f);
            rot.RotateAboutSide(-0.4f);
            rot.ApplyScaleLocal(1.0615f);
            var entity = new MatrixFrame(rot, new Vec3(12f, -4f, 7f, -1f));

            Mat3 poseRot = Mat3.Identity;
            poseRot.RotateAboutForward(1.5f);
            var pose = new MatrixFrame(poseRot, new Vec3(-0.02f, -0.16f, -0.12f, -1f));

            Mat3 arrowRot = Mat3.Identity;
            arrowRot.RotateAboutSide(0.8f);
            var arrow = new MatrixFrame(arrowRot, new Vec3(12.3f, -3.6f, 7.9f, -1f));

            MatrixFrame local = entity.TransformToParent(pose).TransformToLocalNonOrthogonal(arrow);
            MatrixFrame drawn = entity.TransformToParent(pose.TransformToParent(local));

            Expect("the stuck missile lands where it hit", drawn.origin, arrow.origin);
            Expect("... pointing the way it flew", drawn.rotation.f, arrow.rotation.f);
        }

        private static void PatchParameterNames()
        {
            Section("Harmony patch parameters (by name)");

            CheckPrefix("MeleeBlowPatch", "Prefix",
                typeof(Mission), "RegisterBlow",
                new[]
                {
                    typeof(Agent), typeof(Agent), typeof(WeakGameEntity), typeof(Blow),
                    typeof(AttackCollisionData).MakeByRefType(),
                    typeof(MissionWeapon).MakeByRefType(),
                    typeof(CombatLogData).MakeByRefType(),
                });

            CheckPrefix("CombatHitPatch", "Prefix",
                typeof(Agent), "RegisterBlow",
                new[]
                {
                    typeof(Blow), typeof(AttackCollisionData).MakeByRefType(),
                });

            // TEST 261: the block haptic rides the engine's own blocked-hit
            // callback. Its parameters must name the shipped method's
            // parameters exactly, or the patch leaves the build the same silent
            // way MeleeBlowPatch once did.
            CheckPrefix("PlayerBlockHapticPatch", "Postfix",
                typeof(Mission), "OnAgentHitBlocked",
                new[]
                {
                    typeof(Agent), typeof(Agent),
                    typeof(AttackCollisionData).MakeByRefType(),
                    typeof(Vec3), typeof(Vec3), typeof(bool),
                });

            // TEST 262/263: the probe and the ghost both live on this one
            // prefix. Eleven parameters, four of them byref - the exact case
            // MeleeBlowPatch's own history warns about, doubled.
            CheckPrefix("MeleeHitPatch", "Prefix",
                typeof(Mission), "MeleeHitCallback",
                new[]
                {
                    typeof(AttackCollisionData).MakeByRefType(),
                    typeof(Agent), typeof(Agent), typeof(GameEntity),
                    typeof(float).MakeByRefType(),
                    typeof(MeleeCollisionReaction).MakeByRefType(),
                    typeof(CrushThroughState), typeof(Vec3), typeof(Vec3),
                    typeof(HitParticleResultData).MakeByRefType(),
                    typeof(bool),
                });

            // TEST 305: the shield stick. The prefix takes the frame and the
            // shield flag by ref to change them; the target has them by value.
            CheckPrefix("MissileStickPatch", "Prefix",
                typeof(Mission), "HandleMissileCollisionReaction",
                new[]
                {
                    typeof(int), typeof(Mission.MissileCollisionReaction), typeof(MatrixFrame),
                    typeof(bool), typeof(Agent), typeof(Agent), typeof(bool), typeof(sbyte),
                    typeof(MissionObject), typeof(Vec3), typeof(Vec3), typeof(int),
                });

            // TEST 324: every drop's managed choke point.
            Type[] drop =
            {
                typeof(Agent), typeof(EquipmentIndex), typeof(Vec3).MakeByRefType(),
                typeof(Vec3).MakeByRefType(), typeof(Mission.WeaponSpawnFlags), typeof(int),
            };
            CheckPrefix("DropPatch", "Prefix", typeof(Mission), "SpawnWeaponAsDropFromAgentAux", drop);
            CheckPrefix("DropPatch", "Postfix", typeof(Mission), "SpawnWeaponAsDropFromAgentAux", drop);

            // TEST 350: the sheath is stripped where every equip reaches native.
            Type[] equipped =
            {
                typeof(EquipmentIndex), typeof(WeaponData).MakeByRefType(), typeof(WeaponStatsData[]),
                typeof(WeaponData).MakeByRefType(), typeof(WeaponStatsData[]), typeof(WeakGameEntity),
                typeof(bool), typeof(bool),
            };
            CheckPrefix("SheathPatch", "Prefix", typeof(Agent), "WeaponEquipped", equipped);
        }

        private static void GlideAndPickup()
        {
            Section("glide and pickup (TEST 331b/332)");

            // 495: a five-finger pose starts with the hand's seat folded into its x/y/z -
            // turned by the item's grip and the nudge, it lands back on the seat.
            MethodInfo seatInPose = typeof(VrSkinnedWeapon).GetMethod("SeatInPose", BindingFlags.NonPublic | BindingFlags.Static);
            if (seatInPose == null)
            {
                Fail("SeatInPose not found via reflection");
            }
            else
            {
                Mat3 grip = Mat3.Identity;
                grip.RotateAboutUp(0.7f);
                grip.RotateAboutSide(-0.4f);
                Mat3 nudge = Mat3.Identity;
                nudge.RotateAboutForward(0.3f);
                var seat = new Vec3(-0.009f, 0.0075f, 0.0066f, -1f);
                var d = (Vec3)seatInPose.Invoke(null, new object[] { seat, grip, nudge });
                Expect("the folded seat, turned back by the grip and the nudge, is the seat",
                       grip.TransformToParent(nudge.TransformToParent(d)), seat);
            }

            const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo glideAt = typeof(VrSkinnedWeapon).GetMethod("GlideAt", Priv);
            MethodInfo boxDistance = typeof(VrSkinnedWeapon).Assembly.GetType("BannerlordVR.VR.VrGrab")?.GetMethod("BoxDistance", Priv);
            if (glideAt == null || boxDistance == null)
            {
                Fail("GlideAt / BoxDistance not found via reflection");
                return;
            }

            MatrixFrame from = MatrixFrame.Identity;
            from.origin = new Vec3(1f, 2f, 0.1f);
            MatrixFrame to = MatrixFrame.Identity;
            to.rotation.RotateAboutUp(1.2f);
            to.origin = new Vec3(0.3f, 2.4f, 1.3f);

            Func<float, MatrixFrame> at = t => (MatrixFrame)glideAt.Invoke(null, new object[] { from, to, t });

            MatrixFrame a = at(0f), b = at(1f);
            Report((a.origin - from.origin).Length < 1e-4f && Vec3.DotProduct(a.rotation.f, from.rotation.f) > 0.9999f,
                   "the glide starts exactly where the weapon was seen");
            Report((b.origin - to.origin).Length < 1e-4f && Vec3.DotProduct(b.rotation.f, to.rotation.f) > 0.9999f,
                   "the glide ends exactly in the hand");

            bool monotonic = true;
            float last = -1f;
            for (int k = 0; k <= 20; k++)
            {
                MatrixFrame f = at(k / 20f);
                float along = (f.origin - from.origin).Length;
                monotonic &= along >= last - 1e-5f && Math.Abs(f.rotation.f.Length - 1f) < 1e-3f;
                last = along;
            }
            Report(monotonic, "the glide only ever closes in, and stays a rotation");

            Vec3 min = new Vec3(0f, 0f, 0f), max = new Vec3(2f, 0.1f, 0.1f);
            float inside = (float)boxDistance.Invoke(null, new object[] { new Vec3(1.5f, 0.05f, 0.05f), min, max });
            float above = (float)boxDistance.Invoke(null, new object[] { new Vec3(1.5f, 0.05f, 0.40f), min, max });
            Report(Near(inside, 0f) && Near(above, 0.30f),
                   "a polearm is in reach anywhere along it (inside 0, 0.30 m above reads 0.30)");

            // TEST 328b: the second one-hander's hip is the first's, mirrored.
            MethodInfo mirror = typeof(VrSkinnedWeapon).Assembly.GetType("BannerlordVR.VR.VrHolsterView")
                ?.GetMethod("Mirror", Priv);
            if (mirror == null)
            {
                Fail("VrHolsterView.Mirror not found via reflection");
                return;
            }

            MatrixFrame hip = MatrixFrame.Identity;
            hip.rotation.RotateAboutUp(0.7f);
            hip.rotation.RotateAboutSide(-0.4f);
            hip.origin = new Vec3(0.09f, 0.01f, 0.24f);
            bool allAxes = true;
            for (int axis = 0; axis < 3; axis++)
            {
                MatrixFrame m = (MatrixFrame)mirror.Invoke(null, new object[] { hip, axis });
                MatrixFrame back = (MatrixFrame)mirror.Invoke(null, new object[] { m, axis });
                float det = Vec3.DotProduct(Vec3.CrossProduct(m.rotation.s, m.rotation.f), m.rotation.u);
                allAxes &= Math.Abs(det - 1f) < 1e-3f
                           && (back.origin - hip.origin).Length < 1e-5f
                           && (back.rotation.f - hip.rotation.f).Length < 1e-5f
                           && (back.rotation.u - hip.rotation.u).Length < 1e-5f;
                if (axis == 2)
                    allAxes &= Near(m.origin.z, -0.24f) && Near(m.origin.x, 0.09f)
                               && Near(m.rotation.f.z, -hip.rotation.f.z) && Near(m.rotation.u.z, hip.rotation.u.z);
            }
            Report(allAxes, "the mirrored hip stays a proper rotation, lands on the other side, and mirrors back");
        }

        private static void Holstering()
        {
            Section("body holsters (TEST 271)");

            const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo zoneAt = typeof(VrHolster).GetMethod("ZoneAt", Priv);
            MethodInfo rule = typeof(VrHolster).GetMethod("Rule", Priv);
            if (zoneAt == null || rule == null)
            {
                Fail("VrHolster helpers not found via reflection");
                return;
            }

            // TEST 388: the zones are body-local from the EARS now; the cases below
            // are the same real reaches, logged from the eyes, moved to the ears
            // (the head bone is 0.10 m behind and 0.136 m below the eyes).
            Func<float, float, float, string> at = (x, y, z) =>
                zoneAt.Invoke(null, new object[] { new Vec3(x, y + 0.10f, z + 0.136f) }).ToString();
            Check("over the left shoulder is the bow's", at(-0.18f, -0.18f, -0.12f) == "LeftShoulder");
            Check("over the right shoulder is the right one", at(0.18f, -0.18f, -0.12f) == "RightShoulder");
            Check("the left hip", at(-0.20f, 0.05f, -0.70f) == "LeftHip");
            // TEST 271b: real misses from the 271 log - behind the middle of the head.
            Check("behind the head, just left, is the left shoulder", at(-0.05f, -0.17f, -0.11f) == "LeftShoulder");
            Check("behind the head, just right, is the right shoulder", at(0.02f, -0.18f, -0.12f) == "RightShoulder");
            Check("the front of the shoulder is nothing", at(0.35f, 0.13f, -0.33f) == "None");
            // TEST 271c: a hand beside the head or held out to the side is not the back.
            // TEST 411: the user's rule since 2026-10-06 is "on ear to behind ear" - with the
            // live cfg's front edge ahead of the ears, a hand on the ear is the back.
            Check("beside the ear is the back (TEST 411)", at(0.17f, -0.08f, -0.05f) == "RightShoulder");
            Check("out to the side is nothing", at(-0.45f, -0.15f, -0.10f) == "None");
            // TEST 271d: the shoulder blade (21:28:40) is not the back any more.
            Check("down at the shoulder blade is nothing", at(0.21f, -0.14f, -0.37f) == "None");
            Check("in front of the chest is nothing", at(0f, 0.35f, -0.30f) == "None");
            Check("a guard in front of the face is nothing", at(0.10f, 0.30f, 0f) == "None");
            // TEST 318: the 07:20 misses, 0.12 m behind the eyes, are the shoulder now.
            Check("over the shoulder 0.12 m behind the eyes (TEST 318)", at(-0.17f, -0.12f, 0.00f) == "LeftShoulder");

            Func<ItemObject.ItemTypeEnum, string> where = type =>
            {
                object[] args = { type, null };
                string z = rule.Invoke(null, args).ToString();
                return z + "/" + (args[1] == null ? "any" : args[1].ToString());
            };
            Check("shield: right shoulder, left hand", where(ItemObject.ItemTypeEnum.Shield) == "RightShoulder/Left");
            Check("two-handed: right shoulder, right hand", where(ItemObject.ItemTypeEnum.TwoHandedWeapon) == "RightShoulder/Right");
            // TEST 373c: the left hand holds the crossbow; the right comes to the stock.
            Check("crossbow: right shoulder, left (off) hand", where(ItemObject.ItemTypeEnum.Crossbow) == "RightShoulder/Left");
            Check("bow: left shoulder, either hand", where(ItemObject.ItemTypeEnum.Bow) == "LeftShoulder/any");
            Check("one-handed: left hip, either hand", where(ItemObject.ItemTypeEnum.OneHandedWeapon) == "LeftHip/any");
            Check("arrows are not holstered", where(ItemObject.ItemTypeEnum.Arrows) == "None/any");
        }

        private static void ManualArchery()
        {
            Section("manual archery (TEST 269)");

            const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo bind = typeof(VrBow).GetMethod("Bind", Priv);
            MethodInfo power = typeof(VrBow).GetMethod("Power", Priv);
            MethodInfo parse = typeof(VrBow).GetMethod("ParseZone", Priv);
            MethodInfo inQuiver = typeof(VrHolster).GetMethod("BackZone", Priv);

            if (bind == null || power == null || parse == null || inQuiver == null)
            {
                Fail("VrBow helpers not found via reflection");
                return;
            }

            // The shot is the engine's internal entry point, found by signature.
            // A game update that changes it turns archery off silently - this is
            // where that shows up instead.
            Check("Mission.OnAgentShootMissile binds against the shipped DLL",
                  (bool)bind.Invoke(null, null));

            // Defaults: nock 0.20, full draw 0.65, so the pull range is 0.45 m.
            Check("no pull is no power", Math.Abs((float)power.Invoke(null, new object[] { 0f })) < 1e-4f);
            Check("half the range is half power",
                  Math.Abs((float)power.Invoke(null, new object[] { 0.225f }) - 0.5f) < 1e-3f);
            Check("past full draw clamps to 1",
                  Math.Abs((float)power.Invoke(null, new object[] { 2f }) - 1f) < 1e-4f);

            var zone = (Vec3?)parse.Invoke(null, new object[] { "0.15, -0.20,-0.05" });
            Check("a vector parses", zone.HasValue && Math.Abs(zone.Value.y + 0.20f) < 1e-4f);
            Check("an empty vector is off", !((Vec3?)parse.Invoke(null, new object[] { "" })).HasValue);
            Check("two numbers is not a vector", !((Vec3?)parse.Invoke(null, new object[] { "1,2" })).HasValue);

            // TEST 269f: the quiver is behind the head, over either shoulder. The
            // cases are real hand positions from the 269e log (right positive).
            // TEST 388: VrHolster.BackZone, from the ears (the eye-logged cases moved).
            Func<float, float, float, bool> quiver = (x, y, z) =>
                (bool)inQuiver.Invoke(null, new object[] { new Vec3(x, y + 0.10f, z + 0.136f) });
            Check("over the right shoulder grabs", quiver(0.29f, -0.20f, -0.11f));
            Check("over the left shoulder grabs", quiver(-0.09f, -0.16f, -0.09f));
            Check("in front of the face does not", !quiver(0.13f, 0.17f, -0.09f));
            Check("the side of the shoulder does not", !quiver(0.23f, -0.06f, -0.29f));
            Check("behind the hip does not", !quiver(0.20f, -0.20f, -0.60f));

            // TEST 388: the user's rule - never ahead of the ears, either shoulder.
            Func<float, float, float, bool> ears = (x, y, z) =>
                (bool)inQuiver.Invoke(null, new object[] { new Vec3(x, y, z) });
            // TEST 411: the front edge is back_zone_behind_m from the LIVE cfg - "on ear to
            // behind ear" since 2026-10-06, so it may sit ahead of the ear line.
            float front = -(float)typeof(VrHolster).GetField("BackBehindM", Priv).GetValue(null);
            Check("a hand 1 mm ahead of the zone's front edge is never the back", !ears(0.10f, front + 0.001f, -0.05f) && !ears(-0.10f, front + 0.001f, -0.05f));
            Check("just behind the front edge, over either shoulder, is the back", ears(0.15f, front - 0.03f, -0.05f) && ears(-0.15f, front - 0.03f, -0.05f));
            // TEST 411: the PSVR2 log's misses (2026-10-06 14:06): on the ear now grabs,
            // beside the face still does not.
            Check("an on-the-ear reach is the back", ears(-0.18f, 0.00f, -0.02f) && ears(0.18f, 0.00f, 0.05f) && ears(0.19f, 0.07f, 0.00f));
            Check("a hand beside the face is not the back", !ears(-0.16f, 0.14f, -0.01f));

            // TEST 388: the body frame is the entity's axes turned by the chest.
            MethodInfo axes = typeof(VrBodyBones).GetMethod("BodyAxes", Priv);
            if (axes == null)
            {
                Fail("VrBodyBones.BodyAxes not found via reflection");
            }
            else
            {
                Mat3 entity = Mat3.Identity;
                entity.RotateAboutUp(0.7f);
                Mat3 rest = Mat3.Identity;
                rest.RotateAboutSide(1.1f);         // a bone with its own axis convention
                Mat3 chest = rest;
                Mat3 still = (Mat3)axes.Invoke(null, new object[] { entity, chest, rest });
                Check("an unturned chest gives the entity's own axes",
                      (still.f - entity.f).Length < 1e-4f && (still.s - entity.s).Length < 1e-4f);
                Mat3 yaw = Mat3.Identity;
                yaw.RotateAboutUp(0.785f);          // the chest turned 45 deg in the entity's frame
                chest = Mat3.Identity;
                chest.s = yaw.TransformToParent(rest.s);
                chest.f = yaw.TransformToParent(rest.f);
                chest.u = yaw.TransformToParent(rest.u);
                Mat3 turned = (Mat3)axes.Invoke(null, new object[] { entity, chest, rest });
                Vec3 want = entity.TransformToParent(yaw.f);
                Check("a chest turned 45 deg turns the body frame 45 deg, whatever the bone's axes",
                      (turned.f - want).Length < 1e-3f && Math.Abs(turned.u.z - 1f) < 1e-3f);
            }

            // TEST 269b: the arrow mesh placed nock-in-hand, shaft along the draw.
            // A mesh authored along -X, nock at x = +0.4, tip at x = -0.4.
            MethodInfo arrowFrame = typeof(VrBow).GetMethod("ArrowFrame", Priv);
            if (arrowFrame == null)
            {
                Fail("VrBow.ArrowFrame not found via reflection");
                return;
            }

            MatrixFrame local = MatrixFrame.Identity;
            local.origin = new Vec3(0.4f, 0f, 0f);
            local.rotation.f = new Vec3(-1f, 0f, 0f);
            local.rotation.u = new Vec3(0f, 0f, 1f);
            local.rotation.Orthonormalize();

            Vec3 hand = new Vec3(10f, 20f, 1.5f);
            Vec3 draw = new Vec3(0.6f, 0.8f, 0f);
            var placed = (MatrixFrame)arrowFrame.Invoke(null, new object[] { hand, draw, local });

            Expect("the nock sits in the string hand",
                   placed.TransformToParent(new Vec3(0.4f, 0f, 0f)), hand);
            Expect("the tip lies 0.8 m along the draw line",
                   placed.TransformToParent(new Vec3(-0.4f, 0f, 0f)), hand + draw * 0.8f);

            // TEST 269d: the bow turned with the arrow. A bow whose forward is
            // local +X and limbs local +Z, held canted and pointing along world
            // +Y, is aimed along a draw that points 45 degrees up to the right.
            MethodInfo turn = typeof(VrBow).Assembly.GetType("BannerlordVR.VR.VrDrawnBow")?.GetMethod("Turn", Priv);
            if (turn == null)
            {
                Fail("VrDrawnBow.Turn not found via reflection");
                return;
            }

            MatrixFrame b0 = MatrixFrame.Identity;
            b0.rotation.s = new Vec3(0f, 1f, 0f);      // local +X (forward) -> world +Y
            b0.rotation.f = new Vec3(-1f, 0f, 0f);
            b0.rotation.u = new Vec3(0f, 0f, 1f);      // local +Z (limbs) -> world up
            b0.origin = new Vec3(5f, 5f, 1.4f);
            Vec3 pivot = new Vec3(5f, 5.05f, 1.4f);
            Vec3 aim = new Vec3(0.7071f, 0f, 0.7071f);
            var args = new object[] { b0, new Vec3(1f, 0f, 0f), new Vec3(0f, 0f, 1f), aim, pivot, null };

            Check("a draw across the limbs turns the bow", (bool)turn.Invoke(null, args));
            var aimed = (MatrixFrame)args[5];
            Expect("the bow's forward lies along the arrow",
                   aimed.rotation.TransformToParent(new Vec3(1f, 0f, 0f)).NormalizedCopy(), aim);
            Expect("the grip stays in the hand",
                   aimed.TransformToParent(b0.TransformToLocal(pivot)), pivot);
            Check("the limbs stay square to the arrow",
                  Math.Abs(Vec3.DotProduct(aimed.rotation.TransformToParent(new Vec3(0f, 0f, 1f)), aim)) < 1e-3f);
        }

        // -- TEST 352/354: the head-driven body's pelvis rule and the feet's step --
        private static void HeadDrivenBody()
        {
            Section("head-driven body and planted feet (TEST 352/354)");

            const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
            Type body = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrHeadBody");
            MethodInfo bendM = body?.GetMethod("Bend", Priv);
            MethodInfo stepPart = body?.GetMethod("StepPart", Priv);
            MethodInfo stepArc = body?.GetMethod("StepArc", Priv);
            MethodInfo stepDue = body?.GetMethod("StepDue", Priv);
            MethodInfo reachable = body?.GetMethod("Reachable", Priv);
            if (bendM == null || stepPart == null || stepArc == null || stepDue == null || reachable == null)
            {
                Fail("VrHeadBody.Bend/StepPart/StepArc/StepDue/Reachable not found via reflection");
                return;
            }

            // TEST 352b: a head above the body's reach lowers the chest; it never
            // tips the trunk back toward the unreachable point.
            Vec3 high = new Vec3(0f, -0.08f, 0.60f);
            Vec3 got = (Vec3)reachable.Invoke(null, new object[] { Vec3.Zero, high, 0.45f });
            Report(Near(got.y, -0.08f) && Near(got.Length, 0.45f) && got.z > 0.43f,
                   "out of reach upward: lowered to the spine's length, the 8 cm back kept (not tipped to 0.60 m away)");
            Vec3 near = new Vec3(0.1f, 0.1f, 0.3f);
            Report(((Vec3)reachable.Invoke(null, new object[] { Vec3.Zero, near, 0.45f }) - near).Length < 1e-6f,
                   "a reachable chest is left exactly where it is");

            // TEST 352c: the eye's move from standing = a bend about the hips plus
            // a crouch. The standing eye is 0.12 m ahead of the hips, 0.75 m up.
            Vec3 eyeFromHips = new Vec3(0f, 0.12f, 0.75f);
            Func<Vec3, float[]> bend = off =>
            {
                object[] args = { eyeFromHips, off, 0.65f, 0f, 0f, 0f, Vec3.Zero, 0f, 0f };
                bendM.Invoke(null, args);
                return new[] { (float)args[5] * 57.29578f, (float)args[7] };
            };
            Func<double, Vec3> hinged = deg =>
            {
                double r = deg * Math.PI / 180.0;
                return new Vec3(0f, (float)(0.12 * Math.Cos(r) + 0.75 * Math.Sin(r)),
                                (float)(-0.12 * Math.Sin(r) + 0.75 * Math.Cos(r))) - eyeFromHips;
            };

            float[] still = bend(Vec3.Zero);
            Report(Near(still[0], 0f) && Near(still[1], 0f), "standing: no bend, no crouch");

            float[] squat = bend(new Vec3(0f, 0f, -0.30f));
            Report(Near(squat[0], 0f) && Near(squat[1], 0.30f), "straight down 0.30 m: the hips drop 0.30 m, the trunk stays upright");

            float[] bow = bend(hinged(40.0));
            Report(Math.Abs(bow[0] - 40f) < 0.05f && bow[1] < 1e-3f, "a 40 deg bow about the hips: 40 deg of bend, no crouch");

            float[] both = bend(hinged(40.0) + new Vec3(0f, 0f, -0.20f));
            Report(Math.Abs(both[0] - 40f) < 0.05f && Math.Abs(both[1] - 0.20f) < 1e-3f,
                   "bow and squat together: the bend and the crouch both come out");

            float[] deep = bend(new Vec3(0f, 0f, -2f));
            Report(Near(deep[1], 0.65f), "the crouch is capped (body_head_pelvis_clamp)");

            Func<Vec3, float> step = off => ((Vec3)stepPart.Invoke(null, new object[] { eyeFromHips, off, 0f })).Length;
            Report(Math.Abs(step(new Vec3(0f, 0.20f, 0f)) - 0.20f) < 1e-3f,
                   "a level head moving 0.20 m forward is a 0.20 m step (no bend moves a head forward without lowering it)");
            Report(step(hinged(40.0)) < 2e-3f && step(hinged(40.0) + new Vec3(0f, 0f, -0.2f)) < 2e-3f,
                   "a bow (with or without a squat) is no step: the feet stay");
            Report(step(Vec3.Zero) < 1e-6f, "standing still is no step");

            // TEST 352f: leaning back stops early (body_back_bend_deg 5), so a real
            // step back moves the body back instead of arching the chest.
            Report(step(new Vec3(0f, -0.20f, 0f)) > 0.10f,
                   "a 0.20 m step back is mostly a step, not a lean back (" + step(new Vec3(0f, -0.20f, 0f)).ToString("F3") + " m)");
            float[] back = bend(new Vec3(0f, -0.20f, 0f));
            Report(back[0] <= 5.01f, "leaning back is capped at body_back_bend_deg, 5 or less (" + back[0].ToString("F1") + " deg)");

            Vec3 from = new Vec3(0.1f, 0.2f, 0f), to = new Vec3(0.3f, 0.5f, 0.02f);
            Func<float, Vec3> arc = t => (Vec3)stepArc.Invoke(null, new object[] { from, to, t, 0.08f });
            Report((arc(0f) - from).Length < 1e-5f && (arc(1f) - to).Length < 1e-5f,
                   "a step starts on the planted spot and lands exactly on the new one");
            Report(Near(arc(0.5f).z, (from.z + to.z) * 0.5f + 0.08f), "mid-step the foot is lifted by foot_step_lift_m");

            Func<float, float, float, bool> due = (drift, turn, stretch) =>
                (bool)stepDue.Invoke(null, new object[] { drift, turn, stretch, 0.22f, (float)(Math.PI / 4.0) });
            Report(!due(0.10f, 0.2f, 0.9f), "a small drift and turn keep the foot planted");
            Report(due(0.25f, 0f, 0.9f) && due(0f, 0.9f, 0.9f) && due(0f, 0f, 1.05f),
                   "drift past 0.22 m, a turn past 45 deg, or an over-stretched leg each take a step");
        }

        /// <summary>TEST 381-383: soft reach on every two-bone solve, the crouch
        /// dead-band, the hips sitting back, and the lean-back cap where the spine
        /// is aimed. All pure, reached by reflection where private.</summary>
        private static void SmoothBody()
        {
            Section("smooth body: soft reach, crouch dead-band, hips back, lean cap (TEST 381-383)");

            const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
            Type body = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrHeadBody");
            Type arm = typeof(VrArmIk);
            MethodInfo soft = arm.GetMethod("SoftReach", Priv);
            MethodInfo deadM = body?.GetMethod("DeadBand", Priv);
            MethodInfo capM = body?.GetMethod("CapLean", Priv);
            MethodInfo bendM = body?.GetMethod("Bend", Priv);
            MethodInfo stepM = body?.GetMethod("StepPart", Priv);
            if (soft == null || deadM == null || capM == null || bendM == null || stepM == null)
            {
                Fail("SoftReach/DeadBand/CapLean/Bend/StepPart not found via reflection");
                return;
            }

            // 381: soft reach.
            Func<float, float> sr = d => (float)soft.Invoke(null, new object[] { d, 0.9f, 0.05f });
            float start = 0.9f * 0.95f;
            Report(Near(sr(0.5f), 0.5f) && Near(sr(start), start), "soft reach: untouched below reach x (1 - ik_soft)");
            Report(sr(0.9f) < 0.9f && sr(9f) <= 0.9f && sr(9f) > sr(0.9f), "soft reach: short of full stretch at full stretch, never past it, still rising");
            float slope = (sr(start + 1e-3f) - sr(start - 1e-3f)) / 2e-3f;
            Report(Math.Abs(slope - 1f) < 0.02f, "soft reach: no kink at the seam (slope " + slope.ToString("F3") + ")");
            Report(Near((float)soft.Invoke(null, new object[] { 1.2f, 0.9f, 0f }), 1.2f), "ik_soft 0 is the old straight reach");

            // The point of it: a millimetre of target noise at full stretch.
            Vec3 sh = Vec3.Zero, pole = new Vec3(0f, 1f, 0f);
            Func<float, float, Vec3> knee = (d, k) =>
            {
                VrArmIk.SolveElbowWithBend(sh, new Vec3(0f, 0f, -d), pole, 0.45f, 0.45f, out Vec3 e, out _, out _, k);
                return e;
            };
            float hard = (knee(0.899f, 0f) - knee(0.898f, 0f)).Length * 1000f;
            float eased = (knee(0.899f, 0.05f) - knee(0.898f, 0.05f)).Length * 1000f;
            Report(hard > 5f && eased < 2f,
                   "1 mm of noise at full stretch moves the knee " + hard.ToString("F1") + " mm hard, "
                   + eased.ToString("F2") + " mm soft");

            // 382: crouch dead-band.
            Func<float, float> db = x => (float)deadM.Invoke(null, new object[] { x, 0.015f });
            float dbSlope = (db(0.015f + 1e-4f) - db(0.015f - 1e-4f)) / 2e-4f;
            Report(db(-0.1f) == 0f && db(0.002f) < 2e-4f && Near(db(0.015f), 0.0075f) && Near(db(0.3f), 0.2925f),
                   "crouch dead-band: 2 mm of head drop is a 0.13 mm crouch, 0.30 m is 0.2925 m");
            Report(Math.Abs(dbSlope - 1f) < 0.01f, "crouch dead-band: no kink where it ends");

            // 383: the hips sit back, and Bend and StepPart agree on the rest.
            Vec3 v = new Vec3(0f, 0.12f, 0.75f);
            Func<Vec3, float, float[]> bend = (off, cap) =>
            {
                object[] args = { v, off, 0.65f, 0f, cap, 0f, Vec3.Zero, 0f, 0f };
                bendM.Invoke(null, args);
                return new[] { (float)args[5], (float)args[8] };
            };
            Func<Vec3, float, float> step = (off, cap) => ((Vec3)stepM.Invoke(null, new object[] { v, off, cap })).Length;
            bool agree = true;
            string seen = "";
            foreach (float back in new[] { 0.03f, 0.06f, 0.10f, 0.20f, 0.35f })
            {
                Vec3 off = new Vec3(0f, -back, 0f);
                float slide = bend(off, 0.08f)[1];
                float left = step(off, 0f) - slide - step(off, 0.08f);
                agree &= Math.Abs(left) < 1e-3f && slide <= 0.08f + 1e-5f;
                seen += " " + back.ToString("F2") + ":" + slide.ToString("F3") + "/" + step(off, 0.08f).ToString("F3");
            }
            Report(agree, "a move back: the capped bend, then the hips (<= 0.08 m), then a step - Bend and StepPart agree (back:slide/step" + seen + ")");
            Report(step(new Vec3(0f, -0.05f, 0f), 0.08f) < 1e-4f, "a 5 cm lean back is no step at all: the hips take it");
            Report(bend(new Vec3(0f, 0.20f, 0f), 0.08f)[1] == 0f && bend(new Vec3(0.20f, 0f, 0f), 0.08f)[1] == 0f,
                   "forward or sideways moves never slide the hips");

            // 383: the spine is never aimed back past the cap.
            float cap3 = 3f * (float)(Math.PI / 180.0);
            Func<Vec3, Vec3> capped = c => (Vec3)capM.Invoke(null, new object[] { Vec3.Zero, c, cap3 });
            Vec3 lean = new Vec3(0.02f, -0.45f * (float)Math.Sin(0.2), 0.45f * (float)Math.Cos(0.2));
            Vec3 got = capped(lean);
            float tilt = (float)Math.Atan2(-got.y, got.z);
            Report(Math.Abs(tilt - cap3) < 1e-4f && Near(got.Length, lean.Length) && Near(got.x, lean.x),
                   "a chest 11.5 deg behind the hips is aimed 3 deg back, same length, same side");
            Vec3 fwd = new Vec3(0f, 0.2f, 0.4f), up = new Vec3(0f, -0.01f, 0.45f);
            Report((capped(fwd) - fwd).Length < 1e-6f && (capped(up) - up).Length < 1e-6f,
                   "a forward chest, or one within the cap, is left exactly where it is");
        }

        /// <summary>TEST 447/449/450: the chest's spring, the anchor's glide across
        /// a source change, and the arm stretch. All pure, reached by reflection.</summary>
        private static void BodyWeightArmLength()
        {
            Section("chest weight, anchor seam, arm length (TEST 447/449/450)");

            const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
            Type body = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrHeadBody");
            MethodInfo springM = body?.GetMethod("Spring", Priv);
            MethodInfo glideM = typeof(VrCameraDriver).GetMethod("Glide", Priv);
            MethodInfo stretchM = typeof(VrArmIk).GetMethod("Stretch", Priv);
            if (springM == null || glideM == null || stretchM == null)
            {
                Fail("VrHeadBody.Spring / VrCameraDriver.Glide / VrArmIk.Stretch not found via reflection");
                return;
            }

            // 447: a unit step through the chest's spring at 90 Hz.
            const float T = 0.25f, Dt = 1f / 90f;
            float x = 0f, v = 0f, peak = 0f, at4 = 0f;
            for (int i = 1; i <= 180; i++)
            {
                object[] a = { x, v, 1f, T, Dt };
                x = (float)springM.Invoke(null, a);
                v = (float)a[1];
                peak = Math.Max(peak, x);
                if (i == (int)(4f * T / Dt))
                    at4 = x;
            }
            Report(peak <= 1f + 1e-5f && at4 > 0.98f,
                   "chest spring: a step never overshoots (peak " + peak.ToString("F4") + ") and is within 2% after 4x its time ("
                   + at4.ToString("F3") + ")");
            object[] inst = { 0f, 0.5f, 1f, 0f, Dt };
            Report(Near((float)springM.Invoke(null, inst), 1f) && (float)inst[1] == 0f, "chest spring: weight 0 is instant");

            // 449: the saddle's eye hands over to the standing eye 0.8 m lower.
            Vec3 seam = Vec3.Zero, last = Vec3.Zero;
            Func<Vec3, bool, Vec3> glide = (now, changed) =>
            {
                object[] a = { seam, last, now, changed, Dt, 0.3f };
                Vec3 s = (Vec3)glideM.Invoke(null, a);
                seam = (Vec3)a[0];
                last = (Vec3)a[1];
                return now + s;
            };
            Vec3 saddle = new Vec3(0f, 0f, 2.5f), standing = new Vec3(0f, 0.1f, 1.7f);
            glide(saddle, false);
            Vec3 first = glide(standing, true);
            Vec3 after = first;
            for (int i = 0; i < 90; i++)
                after = glide(standing, false);
            float left = (after - standing).Length / (saddle - standing).Length;
            Report((first - saddle).Length < 1e-5f && left < 0.05f,
                   "anchor seam: the view does not move on the frame the source changes, and 1 s later is "
                   + left.ToString("F3") + " of the jump from the new source");

            // 450: a turned, uniformly scaled bone, stretched along a skewed local axis.
            Mat3 r = Mat3.Identity;
            r.RotateAboutAnArbitraryVector(new Vec3(0.3f, 0.5f, 0.8f).NormalizedCopy(), 0.7f);
            r.ApplyScaleLocal(new Vec3(1.06f, 1.06f, 1.06f));
            Vec3 ax = new Vec3(0.9f, 0.3f, 0.1f).NormalizedCopy();
            Vec3 perp = Vec3.CrossProduct(ax, new Vec3(0f, 0f, 1f)).NormalizedCopy();
            Func<Mat3, float, Mat3> stretch = (m, k) => (Mat3)stretchM.Invoke(null, new object[] { m, ax, k });
            Func<Mat3, Mat3, bool> same = (p, q) =>
                (p.s - q.s).Length < 1e-5f && (p.f - q.f).Length < 1e-5f && (p.u - q.u).Length < 1e-5f;
            Mat3 once = stretch(r, 1.2f);
            Report(Near(once.TransformToParent(ax).Length, 1.2f * 1.06f) && Near(once.TransformToParent(perp).Length, 1.06f),
                   "arm stretch: x1.20 along the bone, the girth untouched");
            Report(same(stretch(once, 1.2f), once), "arm stretch: applied again it changes nothing (a paused skeleton cannot compound it)");
            Report(same(stretch(once, 1f), r), "arm stretch: x1.00 puts the bone back exactly");

            // 456: the even scale - every axis by the same factor, set not multiplied.
            MethodInfo scaleM = typeof(VrArmIk).GetMethod("ScaleTo", Priv);
            if (scaleM == null)
            {
                Fail("VrArmIk.ScaleTo not found via reflection");
                return;
            }
            Func<Mat3, float, Mat3> scaleTo = (m, len) => (Mat3)scaleM.Invoke(null, new object[] { m, ax, len });
            Mat3 even = scaleTo(r, 1.06f * 1.06f);
            float sAlong = even.TransformToParent(ax).Length, sAcross = even.TransformToParent(perp).Length;
            Report(Near(sAlong, 1.06f * 1.06f) && Near(sAcross, 1.06f * 1.06f)
                   && Math.Abs(Vec3.DotProduct(even.s, even.f)) < 1e-5f && Math.Abs(Vec3.DotProduct(even.f, even.u)) < 1e-5f,
                   "arm scale: x1.06 evenly - the same along and across, still square (a renderer can take it)");
            Report(same(scaleTo(even, 1.06f * 1.06f), even) && same(scaleTo(even, 1.06f), r),
                   "arm scale: applied again it changes nothing, and back to the bone's own length is the bone exactly");

            // 453: the elbow keeps its side on a tie, and walks freely inside one legal arc.
            MethodInfo bestM = typeof(VrArmBend).GetMethod("Best", Priv);
            Type ruleT = typeof(VrArmBend).GetNestedType("Rule", BindingFlags.NonPublic);
            if (bestM == null || ruleT == null)
            {
                Fail("VrArmBend.Best / Rule not found via reflection");
                return;
            }
            Vec3 cu = new Vec3(1f, 0f, 0f), cv = new Vec3(0f, 1f, 0f);
            Func<float, Vec3> at = deg => cu * (float)Math.Cos(deg * Math.PI / 180.0) + cv * (float)Math.Sin(deg * Math.PI / 180.0);
            Array rules = Array.CreateInstance(ruleT, 3);
            object rule = Activator.CreateInstance(ruleT);
            ruleT.GetField("N", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(rule, cu);
            ruleT.GetField("Min", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(rule, 0.9f);
            ruleT.GetField("Weight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(rule, 1f);
            rules.SetValue(rule, 0);
            Func<Vec3, Vec3, int, Vec3> best = (wanted, prefer, n) =>
                (Vec3)bestM.Invoke(null, new object[] { wanted, cu, cv, rules, n, prefer });
            // The legal arc is within 25.8 deg of u and the wanted elbow is opposite it:
            // two equally good answers, at +25.8 and -25.8 deg.
            bool keepsUp = Vec3.DotProduct(best(-cu, at(20f), 1), cv) > 0f;
            bool keepsDown = Vec3.DotProduct(best(-cu, at(-20f), 1), cv) < 0f;
            // And with them 120 deg apart (legal within 60 deg of u).
            ruleT.GetField("Min", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).SetValue(rule, 0.5f);
            rules.SetValue(rule, 0);
            bool farUp = Vec3.DotProduct(best(-cu, at(55f), 1), cv) > 0f;
            bool farDown = Vec3.DotProduct(best(-cu, at(-55f), 1), cv) < 0f;
            Report(keepsUp && keepsDown && farUp && farDown,
                   "elbow stick: of two equally good elbows (52 or 120 deg apart), the one it already had is kept, either side");
            Vec3 walk = at(90f);
            for (int i = 0; i < 3; i++)
                walk = best(cu, walk, 0);
            Report(Vec3.DotProduct(walk, cu) > 0.9998f, "elbow stick: with nothing in the way it still reaches the wanted elbow (3 frames from 90 deg)");

            // 454: the logged flip spots - the right hand ahead, 0.35-0.6 m out, from
            // the shoulder's line to well across - swept up through shoulder height and
            // back down as the solver runs it (last frame's elbow fed back, TEST 453).
            // The elbow must turn smoothly, at every arm length, and come back down.
            float worst = 0f, worstDown = 1f;
            string worstAt = "";
            foreach (float k in new[] { 1.00f, 1.05f, 1.15f })
            foreach (float ahead in new[] { 0.35f, 0.45f, 0.59f })
            foreach (float side in new[] { -0.25f, -0.20f, -0.08f, 0f })
            {
                float ua = 0.289f * k, fb = 0.268f * k;
                Vec3 prev = Vec3.Zero, startDir = Vec3.Zero, endDir = Vec3.Zero;
                for (int step = 0; step <= 200; step++)
                {
                    float z = step <= 100 ? -0.04f + step * 0.0025f : -0.04f + (200 - step) * 0.0025f;
                    Vec3 tgt = new Vec3(side, ahead, z);
                    float d = tgt.Length;
                    float cosA = Math.Max(-1f, Math.Min(1f, (ua * ua + d * d - fb * fb) / (2f * ua * d)));
                    float sinA = (float)Math.Sqrt(1f - cosA * cosA);
                    Vec3 dir = VrArmBend.Direction(Vec3.Zero, tgt, ua + fb, false, 1f, cosA, sinA, ua,
                                                   0f, default(Vec3), 0f, prev, default(Vec3));
                    // A straight arm (sinA ~0) puts the elbow on the line whichever way it points.
                    if (prev.LengthSquared > 0.5f && dir.LengthSquared > 0.5f && sinA > 0.05f)
                    {
                        float deg = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, Vec3.DotProduct(prev, dir)))) * 180.0 / Math.PI);
                        if (deg > 8f && Environment.GetEnvironmentVariable("BVR_PROBE") == "1")
                            Console.WriteLine("PROBE k " + k + " side " + side + " ahead " + ahead + " z " + z.ToString("F3")
                                + " prev (" + prev.x.ToString("F2") + "," + prev.y.ToString("F2") + "," + prev.z.ToString("F2")
                                + ") now (" + dir.x.ToString("F2") + "," + dir.y.ToString("F2") + "," + dir.z.ToString("F2") + ")");
                        if (deg > worst)
                        {
                            worst = deg;
                            worstAt = "x" + k.ToString("F2") + " at (" + side.ToString("F2") + ", " + ahead.ToString("F2") + ", " + z.ToString("F3") + ")";
                        }
                    }
                    if (step == 0) startDir = dir;
                    endDir = dir;
                    prev = dir;
                }
                worstDown = Math.Min(worstDown, Vec3.DotProduct(startDir, endDir));
            }
            // ponytail: 8-18 deg steps remain where the forward limit and the fading ribs
            // rule trade a boundary point (raw, before the 80 ms filter and 500 deg/s cap
            // ease them). The flips this guards against were 112-180 deg. A softmin over
            // the circle instead of Best's argmin is the upgrade if those steps show.
            Report(worst < 25f, "elbow height rule: no flip - a hand swept up and down through shoulder height turns the elbow at most "
                               + worst.ToString("F1") + " deg per 2.5 mm (" + worstAt + ")");
            Report(worstDown > 0.98f, "elbow stick: swept back down, the elbow is back where it started (worst cos " + worstDown.ToString("F3") + ")");
        }

        private static void CheckPrefix(string patchClass, string patchMethod,
                                        Type targetType, string targetName, Type[] signature)
        {
            MethodInfo target = targetType.GetMethod(
                targetName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, signature, null);

            if (target == null)
            {
                Fail(targetType.Name + "." + targetName + " not found with the expected signature");
                return;
            }

            var names = new System.Collections.Generic.HashSet<string>();
            foreach (ParameterInfo p in target.GetParameters())
                names.Add(p.Name);

            Type patch = typeof(VrMelee).Assembly.GetType(
                "BannerlordVR.Patches." + patchClass);

            MethodInfo prefix = patch?.GetMethod(
                patchMethod, BindingFlags.NonPublic | BindingFlags.Static);

            if (prefix == null)
            {
                Fail(patchClass + "." + patchMethod + " not found");
                return;
            }

            foreach (ParameterInfo p in prefix.GetParameters())
            {
                // __-prefixed names are Harmony's own injection names.
                if (p.Name.StartsWith("__"))
                    continue;

                Report(names.Contains(p.Name), string.Format(
                    "{0}.{1} binds '{2}' on {3}.{4} (target has: {5})",
                    patchClass, patchMethod, p.Name, targetType.Name, targetName,
                    string.Join(", ", names)));
            }
        }

        /// <summary>TEST 309 weight springs and TEST 311's enemy blade from the
        /// item frame. VrWeight's helpers are private and pure, reached by
        /// reflection like VrTwoHand.Steer.</summary>
        // -- TEST 314-317: the solid weapon, and the enemy's look ahead -----------
        //
        // The pure halves: the shield plate's geometry, the push that shares a
        // stop between the hand and the weapon's turn, and the guard an enemy
        // picks for where the blade will land. Everything else is engine calls.
        private static void SolidWeapon()
        {
            Section("solid weapon, enemy look-ahead (TEST 314-317)");

            const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
            Assembly mod = typeof(VrMelee).Assembly;
            Type collide = mod.GetType("BannerlordVR.VR.VrCollide");
            Type guard = mod.GetType("BannerlordVR.VR.VrEnemyGuard");

            MethodInfo span = collide?.GetMethod("PlateSpan", Priv);
            MethodInfo sided = collide?.GetMethod("Sided", Priv);
            MethodInfo touch = collide?.GetMethod("PlateTouch", Priv);
            MethodInfo push = collide?.GetMethod("Push", Priv, null,
                new[] { typeof(MatrixFrame).MakeByRefType(), typeof(Vec3), typeof(Vec3), typeof(float) }, null);
            MethodInfo from = collide?.GetMethod("From", Priv);
            MethodInfo miss = typeof(VrBlade).GetMethod("PlateMiss", Priv);
            MethodInfo fit = typeof(VrBlade).GetMethod("FitPlate", Priv);
            MethodInfo guardFor = guard?.GetMethod("GuardFor", Priv);
            MethodInfo spin = guard?.GetMethod("Spin", Priv);

            if (span == null || sided == null || touch == null || push == null || miss == null
                || fit == null || guardFor == null || spin == null || from == null)
            {
                Fail("TEST 314-317 helpers not found via reflection");
                return;
            }

            // A kite face-on to a player at the origin: 0.5 x 0.7 m, 2 cm thick, its
            // centre 1 m ahead at chest height, its normal toward him.
            var plate = new VrBlade.Plate
            {
                Valid = true,
                Centre = new Vec3(0f, 1f, 1.2f, -1f),
                A = new Vec3(1f, 0f, 0f, -1f),
                B = new Vec3(0f, 0f, 1f, -1f),
                N = new Vec3(0f, -1f, 0f, -1f),
                HalfA = 0.25f,
                HalfB = 0.35f,
                HalfN = 0.01f,
            };

            Approx("a point on the face misses by nothing",
                   (float)miss.Invoke(null, new object[] { new Vec3(0.1f, 0.99f, 1.3f, -1f), plate }), 0f);
            Approx("one 0.1 m in front misses by 0.09",
                   (float)miss.Invoke(null, new object[] { new Vec3(0f, 0.9f, 1.2f, -1f), plate }), 0.09f);

            // A thrust straight through the middle: the blade spans the face, and
            // its tip, 0.5 m past the mid-plane, is the deepest point.
            var grip = new Vec3(0f, 0.5f, 1.2f, -1f);
            var tip = new Vec3(0f, 1.5f, 1.2f, -1f);
            object[] args = { grip, tip, plate, 0.04f, 0f, 0f };
            Check("a thrust through the face spans it", (bool)span.Invoke(null, args));
            object[] deepest = { grip, tip, plate, 1f, args[4], args[5], 0f };
            Approx("... deepest 0.5 m behind the mid-plane", (float)sided.Invoke(null, deepest), -0.5f);
            Approx("... at the tip", (float)deepest[6], 1f);

            object[] beside = { new Vec3(0.4f, 0.5f, 1.2f, -1f), new Vec3(0.4f, 1.5f, 1.2f, -1f), plate, 0.06f, 0f };
            Check("a blade 0.4 m to the side does not touch it", !(bool)touch.Invoke(null, beside));
            object[] through = { grip, tip, plate, 0.06f, 0f };
            Check("the thrust touches it", (bool)touch.Invoke(null, through));
            Approx("... where it crosses, half way", (float)through[4], 0.5f);
            object[] resting = { new Vec3(-0.3f, 0.955f, 1.2f, -1f), new Vec3(0.3f, 0.955f, 1.2f, -1f), plate, 0.06f, 0f };
            Check("a cut resting on the face touches it", (bool)touch.Invoke(null, resting));

            // TEST 335: the push turns the weapon about the hand, exactly, and the
            // hand never moves. A sword caught at the tip, pushed 0.1 m sideways:
            // the tip goes the 0.1 m, keeps its distance, stays in its plane.
            MatrixFrame hand = MatrixFrame.Identity;
            var bladeTip = new Vec3(0f, 0.9f, 0f, -1f);
            object[] cut = { hand, bladeTip, new Vec3(1f, 0f, 0f, -1f), 0.1f };
            push.Invoke(null, cut);
            hand = (MatrixFrame)cut[0];
            Vec3 tipAfter = hand.TransformToParent(bladeTip);
            Check("a tip hit: the hand stays put", hand.origin.Length < 1e-6f);
            Check("... the blade turns the tip the 0.1 m it was pushed", Math.Abs(tipAfter.x - 0.1f) < 1e-4f);
            Check("... at the blade's own length, in its plane",
                  Math.Abs(tipAfter.Length - 0.9f) < 1e-4f && Math.Abs(tipAfter.z) < 1e-4f);

            // A thrust straight into the face has no plane to turn in: it glances
            // off sideways, the tip ending on the face, the hand where it was.
            hand = MatrixFrame.Identity;
            hand.origin = grip;
            object[] stop = { hand, tip, new Vec3(0f, -1f, 0f, -1f), 0.01f + 0.04f + 0.005f + 0.5f };
            push.Invoke(null, stop);
            hand = (MatrixFrame)stop[0];
            Check("a thrust into the shield: the hand stays put", (hand.origin - grip).Length < 1e-6f);
            Check("... and the blade glances off, its tip on the face",
                  Math.Abs(hand.TransformToParent(new Vec3(0f, 1f, 0f, -1f)).y - 0.945f) < 1e-3f);

            // The first 0.12 m out from the hand does not collide: the hand may be
            // in a body, and no turn takes the hilt out of it.
            Vec3 start = (Vec3)from.Invoke(null, new object[] { new Vec3(0f, -0.1f, 0f, -1f), bladeTip, 0.12f });
            Check("the colliding blade starts 0.12 m out from the hand", Math.Abs(start.y - 0.12f) < 1e-5f);

            // TEST 315b: a kite's collision body - 0.55 wide, 1.2 tall, bowed 6 cm,
            // tilted 10 degrees in its own space - fitted back to its face.
            var cloud = new System.Collections.Generic.List<Vec3>();
            float tilt = 10f * (float)Math.PI / 180f;
            for (int i = 0; i <= 10; i++)
            {
                for (int j = 0; j <= 20; j++)
                {
                    float x = -0.275f + 0.055f * i;
                    float z = -0.6f + 0.06f * j;
                    float bow = 0.06f * (1f - (x / 0.275f) * (x / 0.275f));
                    foreach (float y in new[] { bow, bow - 0.02f })
                    {
                        cloud.Add(new Vec3(x, y * (float)Math.Cos(tilt) - z * (float)Math.Sin(tilt),
                                           y * (float)Math.Sin(tilt) + z * (float)Math.Cos(tilt), -1f));
                    }
                }
            }

            object[] fitted = { cloud, Vec3.Zero, Vec3.Zero, Vec3.Zero, Vec3.Zero, 0f, 0f, 0f };
            Check("a tilted, bowed kite fits", (bool)fit.Invoke(null, fitted));
            var face = new Vec3(0f, (float)Math.Cos(tilt), (float)Math.Sin(tilt), -1f);
            Check("... its normal is the face's, within 2 degrees",
                  Math.Abs(Vec3.DotProduct((Vec3)fitted[4], face)) > (float)Math.Cos(2f * Math.PI / 180f));
            Check("... 1.2 m long and 0.55 wide",
                  Math.Abs((float)fitted[5] - 0.6f) < 0.01f && Math.Abs((float)fitted[6] - 0.275f) < 0.01f);
            Check("... and only the bow and the board deep", (float)fitted[7] < 0.05f);

            // TEST 320: the weapon you see keeps up with the controller, never
            // faster than that plus 2 m/s - a stop is not slowed, a release glides.
            MethodInfo catchUp = collide.GetMethod("Catch", Priv, null,
                new[] { typeof(MatrixFrame), typeof(MatrixFrame), typeof(MatrixFrame), typeof(MatrixFrame),
                        typeof(Vec3), typeof(Vec3), typeof(float), typeof(float) }, null);
            if (catchUp == null)
            {
                Fail("VrCollide.Catch not found via reflection");
            }
            else
            {
                const float Tick = 1f / 90f;
                MatrixFrame at = MatrixFrame.Identity;
                MatrixFrame ahead = MatrixFrame.Identity;
                ahead.origin = new Vec3(0.1f, 0f, 0f, -1f);

                var bladeGrip = new Vec3(0f, -0.1f, 0f, -1f);
                var bladeEnd = new Vec3(0f, 0.9f, 0f, -1f);
                Func<MatrixFrame, MatrixFrame, MatrixFrame, MatrixFrame, MatrixFrame> step = (prev, want, prevCtl, ctl) =>
                    (MatrixFrame)catchUp.Invoke(null, new object[] { prev, want, prevCtl, ctl, bladeGrip, bladeEnd, Tick, 2f });

                Check("free air: the weapon is where the controller is",
                      (step(at, ahead, at, ahead).origin - ahead.origin).Length < 1e-5f);

                // TEST 335: a release turns the blade back - 90 degrees off, the
                // controller still: the tip glides 2 m/s, it does not flick.
                MatrixFrame bent = MatrixFrame.Identity;
                bent.rotation = (Mat3)typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrWeight")
                    .GetMethod("Turn", Priv).Invoke(null, new object[] { Mat3.Identity, new Vec3(0f, 0f, 1.5708f, -1f) });
                MatrixFrame back = step(bent, at, at, at);
                float tipMoved = (back.TransformToParent(bladeEnd) - bent.TransformToParent(bladeEnd)).Length;
                Check("a release turns back gliding, not flicking", tipMoved < 2f * Tick * 1.15f && tipMoved > 0f);

                // TEST 323: the controller sweeps on 0.1 m while the shield turns
                // the blade back 6 degrees about the hand - the tip moves less than
                // the controller would carry it, so the turn is not slowed.
                MatrixFrame swept = ahead;
                swept.rotation = (Mat3)typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrWeight")
                    .GetMethod("Turn", Priv).Invoke(null, new object[] { Mat3.Identity, new Vec3(0f, 0f, 0.1f, -1f) });
                MatrixFrame got = step(at, swept, at, ahead);
                Check("a stop that turns the blade is not slowed",
                      (got.origin - swept.origin).Length < 1e-5f && (got.rotation.f - swept.rotation.f).Length < 1e-4f);
            }

            // TEST 314: the guard for where the blade lands, defender-local
            // (x right, y forward, z up from the chest).
            // By value: DefendUp is 4, which the enum also names AttackEnd.
            Func<Vec3, bool, bool, bool, string> pick = (local, stab, head, flip) =>
                ((Agent.UsageDirection)Convert.ToInt32(guardFor.Invoke(null, new object[] { local, stab, head, flip })))
                == Agent.UsageDirection.DefendUp
                    ? "DefendUp"
                    : guardFor.Invoke(null, new object[] { local, stab, head, flip }).ToString();
            var right = new Vec3(0.2f, 0.3f, 0f, -1f);
            var left = new Vec3(-0.2f, 0.3f, 0f, -1f);
            Check("a stab is met low", pick(right, true, false, false) == "DefendDown");
            Check("the head is guarded high", pick(right, false, true, false) == "DefendUp");
            Check("well above the chest is high", pick(new Vec3(0.2f, 0.3f, 0.4f, -1f), false, false, false) == "DefendUp");
            Check("a cut to the right is guarded right", pick(right, false, false, false) == "DefendRight");
            Check("a cut to the left is guarded left", pick(left, false, false, false) == "DefendLeft");
            Check("melee_ai_side_flip swaps them", pick(right, false, false, true) == "DefendLeft");

            var turned = (Vec3)spin.Invoke(null, new object[] { new Vec3(1f, 0f, 0f, -1f),
                                                                new Vec3(0f, 0f, (float)Math.PI / 2f, -1f) });
            Check("the look ahead turns the blade the way it turns",
                  Math.Abs(turned.x) < 1e-4f && Math.Abs(turned.y - 1f) < 1e-4f);
        }

        private static void WeaponWeight()
        {
            Section("weapon weight (TEST 309) and frame blade (TEST 311)");

            Type weight = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrWeight");
            Func<string, MethodInfo> get = name =>
                weight?.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

            MethodInfo spring = get("Spring"), turnSpring = get("SpringTurn"), between = get("Between"),
                       turn = get("Turn"), sag = get("Sag");
            if (spring == null || turnSpring == null || between == null || turn == null || sag == null)
            {
                Fail("VrWeight helpers not found via reflection");
                return;
            }

            // A step of 0.1 m: settles, overshoots under 10% at zeta 0.75, and is
            // stable with a 0.1 s hitch at omega 90.
            var p = Vec3.Zero;
            var v = Vec3.Zero;
            var target = new Vec3(0.1f, 0f, 0f, -1f);
            float peak = 0f;
            for (int i = 0; i < 90; i++)
            {
                object[] args = { p, v, target, 30f, 0.75f, 1f / 90f };
                spring.Invoke(null, args);
                p = (Vec3)args[0];
                v = (Vec3)args[1];
                peak = Math.Max(peak, p.x);
            }

            Check("spring settles on the target", Math.Abs(p.x - 0.1f) < 1e-3f);
            Check("spring overshoot under 10% at zeta 0.75", peak < 0.11f);

            object[] hitch = { Vec3.Zero, Vec3.Zero, target, 90f, 0.75f, 0.1f };
            spring.Invoke(null, hitch);
            Check("spring stable through a 0.1 s hitch at omega 90", Math.Abs(((Vec3)hitch[0]).x) < 0.2f);

            // Between / Turn are inverses, at a small angle, a large one and 180.
            Mat3 a = Mat3.Identity;
            a.RotateAboutUp(0.7f);
            a.RotateAboutSide(-0.3f);
            foreach (float angle in new[] { 0.01f, 1.9f, (float)Math.PI })
            {
                Vec3 rv = new Vec3(0.3f, -0.5f, 0.8f, -1f).NormalizedCopy() * angle;
                var b = (Mat3)turn.Invoke(null, new object[] { a, rv });
                var back = (Vec3)between.Invoke(null, new object[] { a, b });
                var again = (Mat3)turn.Invoke(null, new object[] { a, back });
                Check(string.Format("between/turn round trip at {0:F2} rad", angle),
                      (again.f - b.f).Length < 1e-3f && (again.u - b.u).Length < 1e-3f);
            }

            // The rotation spring brings a turned hand back.
            Mat3 r = (Mat3)turn.Invoke(null, new object[] { Mat3.Identity, new Vec3(0f, 0f, 1f, -1f) });
            Vec3 w = Vec3.Zero;
            for (int i = 0; i < 90; i++)
            {
                object[] args = { r, w, Mat3.Identity, 30f, 0.75f, 1f / 90f };
                turnSpring.Invoke(null, args);
                r = (Mat3)args[0];
                w = (Vec3)args[1];
            }

            Check("rotation spring settles", ((Vec3)between.Invoke(null, new object[] { r, Mat3.Identity })).Length < 1e-2f);

            // TEST 304e: the two-hand ease - half way is half the angle.
            MethodInfo blend = get("Blend");
            if (blend == null)
            {
                Fail("VrWeight.Blend not found via reflection");
            }
            else
            {
                Mat3 to = (Mat3)turn.Invoke(null, new object[] { a, new Vec3(0f, 0.6f, 0.8f, -1f) });
                var start = (Mat3)blend.Invoke(null, new object[] { a, to, 0f });
                var end = (Mat3)blend.Invoke(null, new object[] { a, to, 1f });
                var half = (Mat3)blend.Invoke(null, new object[] { a, to, 0.5f });
                Check("blend 0 is the start, 1 the end",
                      (start.f - a.f).Length < 1e-4f && (end.f - to.f).Length < 1e-3f && (end.u - to.u).Length < 1e-3f);
                Approx("blend 0.5 is half the angle",
                       ((Vec3)between.Invoke(null, new object[] { a, half })).Length, 0.5f);
            }

            // Sag: a level blade tips DOWN, clamped; a vertical one does not droop.
            var down = new Vec3(0f, 0f, -1f, -1f);
            var level = new Vec3(0f, 1f, 0f, -1f);
            var sagged = (Vec3)sag.Invoke(null, new object[] { level, down, 3f, 0.35f, 36f, 25f * (float)Math.PI / 180f });
            var tip = (Mat3)turn.Invoke(null, new object[] { Mat3.Identity, sagged });
            Check("a level heavy blade tips down", tip.TransformToParent(level).z < -0.1f);
            Approx("greatsword sag one-handed (deg)", sagged.Length * 180f / (float)Math.PI,
                   3f * 9.81f * 0.35f / 36f * 180f / (float)Math.PI);

            var huge = (Vec3)sag.Invoke(null, new object[] { level, down, 30f, 1f, 36f, 25f * (float)Math.PI / 180f });
            Approx("sag is clamped (deg)", huge.Length * 180f / (float)Math.PI, 25f);

            var upright = (Vec3)sag.Invoke(null, new object[] { new Vec3(0f, 0f, 1f, -1f), down, 3f, 0.35f, 36f, 1f });
            Check("a vertical blade does not droop", upright.Length < 1e-4f);

            // TEST 304f: the main fist's line mirrored into the off hand - across the
            // body's side axis, and back again to where it started.
            MethodInfo mirror = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrTwoHand")?
                .GetMethod("Mirror", BindingFlags.NonPublic | BindingFlags.Static);
            if (mirror == null)
            {
                Fail("VrTwoHand.Mirror not found via reflection");
            }
            else
            {
                var line = new Vec3(0.3f, -0.2f, 0.9f, -1f);
                Expect("mirror with identity rests negates side",
                       (Vec3)mirror.Invoke(null, new object[] { Mat3.Identity, Mat3.Identity, line }),
                       new Vec3(-0.3f, -0.2f, 0.9f, -1f));

                Mat3 restR = Mat3.Identity;
                restR.RotateAboutUp(0.8f);
                restR.RotateAboutForward(-0.5f);
                Mat3 restL = Mat3.Identity;
                restL.RotateAboutSide(1.1f);
                var there = (Vec3)mirror.Invoke(null, new object[] { restR, restL, line });
                var back = (Vec3)mirror.Invoke(null, new object[] { restL, restR, there });
                Expect("mirror there and back is the same line", back, line);
            }

            // TEST 304h: the slide stops short of the butt, the head and the off hand.
            MethodInfo bounds = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrTwoHand")?
                .GetMethod("SlideBounds", BindingFlags.NonPublic | BindingFlags.Static);
            if (bounds == null)
            {
                Fail("VrTwoHand.SlideBounds not found via reflection");
            }
            else
            {
                // Hand at 0: butt 0.2 behind, tip 1.4 ahead, off hand 0.4 ahead.
                object[] ahead = { 0f, -0.2f, 1.4f, 0.4f, 1f, 0f, 0f };
                bounds.Invoke(null, ahead);
                Approx("off hand ahead: hand stops 0.12 short of it", -(float)ahead[5], 0.28f);
                Approx("off hand ahead: hand stops 0.05 short of the butt", -(float)ahead[6], -0.15f);

                // Off hand 0.3 behind: the hand may go up to 0.25 short of the tip.
                object[] behind = { 0f, -0.5f, 1.4f, -0.3f, -1f, 0f, 0f };
                bounds.Invoke(null, behind);
                Approx("off hand behind: hand stops short of the head", -(float)behind[5], 1.15f);
                Approx("off hand behind: hand stops 0.12 short of it", -(float)behind[6], -0.18f);
            }

            // TEST 460: the haft is the mesh's own z, signed like the box's axis,
            // unless the mesh is not along its z at all.
            MethodInfo haft = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrTwoHand")?
                .GetMethod("HaftAxis", BindingFlags.NonPublic | BindingFlags.Static);
            if (haft == null)
            {
                Fail("VrTwoHand.HaftAxis not found via reflection");
            }
            else
            {
                var box = new Vec3(0f, 0f, 1f);
                float tiltRad = 3.1f * (float)(Math.PI / 180.0);
                var tilted = new Vec3(0f, (float)Math.Sin(tiltRad), (float)Math.Cos(tiltRad));

                object[] near = { box, tilted * 2f, 0f };
                Expect("haft: a 3.1 deg tilt is the mesh's z, unit length", (Vec3)haft.Invoke(null, near), tilted);
                Check("haft: the tilt is reported in degrees (" + ((float)near[2]).ToString("F3") + ")",
                      Math.Abs((float)near[2] - 3.1f) < 0.01f);

                object[] flipped = { box, tilted * -1f, 0f };
                Expect("haft: a flipped mesh z is signed like the box", (Vec3)haft.Invoke(null, flipped), tilted);

                object[] across = { box, new Vec3(1f, 0f, 0f), 0f };
                Expect("haft: a mesh z 90 deg off keeps the box's axis", (Vec3)haft.Invoke(null, across), box);
            }

            // TEST 465: the draw is the arrow less the tip past the rest; no limit
            // before the arrow is measured.
            MethodInfo maxDraw = typeof(VrBow).GetMethod("MaxDraw", BindingFlags.NonPublic | BindingFlags.Static);
            if (maxDraw == null)
            {
                Fail("VrBow.MaxDraw not found via reflection");
            }
            else
            {
                Approx("bow: a 0.97 m arrow draws to 0.91 m", (float)maxDraw.Invoke(null, new object[] { 0.97f, 0.06f }), 0.91f);
                Check("bow: no arrow measured, no limit", (float)maxDraw.Invoke(null, new object[] { 0f, 0.06f }) > 100f);
            }

            // controls_swap_trigger_grip: values and button bits trade places, the
            // rest of the bits stay.
            MethodInfo swap = typeof(VrMelee).Assembly.GetType("BannerlordVR.Interop.NativeMethods")?
                .GetMethod("SwapTriggerAndGrip", BindingFlags.NonPublic | BindingFlags.Static);
            if (swap == null)
            {
                Fail("NativeMethods.SwapTriggerAndGrip not found via reflection");
            }
            else
            {
                var hand = new BannerlordVR.Interop.BvrHandState
                {
                    Trigger = 0.9f,
                    Grip = 0.1f,
                    Buttons = (int)BannerlordVR.Interop.BvrButton.Trigger | (int)BannerlordVR.Interop.BvrButton.Menu,
                };
                object[] args = { hand };
                swap.Invoke(null, args);
                var swapped = (BannerlordVR.Interop.BvrHandState)args[0];
                Check("trigger and grip swap values and bits", swapped.Trigger == 0.1f && swapped.Grip == 0.9f
                      && swapped.Buttons == ((int)BannerlordVR.Interop.BvrButton.Grip | (int)BannerlordVR.Interop.BvrButton.Menu));
            }

            // TEST 311: the enemy blade from the item frame runs from its origin
            // along the frame's up axis.
            MethodInfo frameBlade = typeof(VrBlade).GetMethod("FrameBlade", BindingFlags.NonPublic | BindingFlags.Static);
            if (frameBlade == null)
            {
                Fail("VrBlade.FrameBlade not found via reflection");
                return;
            }

            Mat3 frameRot = Mat3.Identity;
            frameRot.RotateAboutSide(0.5f);
            var frame = new MatrixFrame(frameRot, new Vec3(0.02f, -0.01f, 0.03f, -1f));
            object[] blade = { frame, 1.1f, Vec3.Zero, Vec3.Zero };
            frameBlade.Invoke(null, blade);
            Expect("frame blade grip is the frame origin", (Vec3)blade[2], frame.origin);
            Expect("frame blade tip is origin + u x length", (Vec3)blade[3],
                   frame.origin + frameRot.u.NormalizedCopy() * 1.1f);
        }

        /// <summary>TEST 371-373: the release solve, the throw's aim, the
        /// crossbow string's 2 s, and the two-handed hold.</summary>
        private static void ThrowAndCrossbow()
        {
            Section("throwing and the crossbow (TEST 371-373)");

            const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
            Type throwType = typeof(VrSkinnedWeapon).Assembly.GetType("BannerlordVR.VR.VrThrow");
            Type xbowType = typeof(VrSkinnedWeapon).Assembly.GetType("BannerlordVR.VR.VrCrossbow");
            MethodInfo solve = throwType?.GetMethod("Solve", Priv);
            MethodInfo ballistic = throwType?.GetMethod("Ballistic", Priv);
            MethodInfo bend = throwType?.GetMethod("Bend", Priv);
            MethodInfo span = xbowType?.GetMethod("Span", Priv);
            MethodInfo align = xbowType?.GetMethod("Align", Priv);
            if (solve == null || ballistic == null || bend == null || span == null || align == null)
            {
                Fail("VrThrow.Solve/Ballistic/Bend or VrCrossbow.Span/Align not found via reflection");
                return;
            }

            Func<Vec3, Vec3, float> degrees = (a, b) => (float)(Math.Acos(Math.Max(-1f, Math.Min(1f,
                Vec3.DotProduct(a.NormalizedCopy(), b.NormalizedCopy())))) * 180.0 / Math.PI);

            // A throw at 90 Hz: the hand speeds up to 8 m/s along `along`, then
            // slows to 5 m/s over the last 30 ms before the hand opens.
            Vec3 along = new Vec3(0.6f, 0.7f, 0.39f).NormalizedCopy();
            var times = new float[24];
            var vels = new Vec3[24];
            for (int k = 0; k < 24; k++)
            {
                times[k] = k / 90f;
                float s = k < 21 ? 8f * (k + 1) / 21f : 8f - (k - 20) * 1f;
                Vec3 wobble = new Vec3(0.02f * (k % 3 - 1), -0.02f * (k % 2), 0f);
                vels[k] = along * s + wobble;
            }

            object[] args = { times, vels, 24, 23 / 90f, 0.1f, 0f, Vec3.Zero };
            bool solved = (bool)solve.Invoke(null, args);
            float peak = (float)args[5];
            Vec3 dir = (Vec3)args[6];
            Check("the release reads the swing's peak, not the slower release frame (got "
                  + peak.ToString("F2") + " m/s)", solved && Math.Abs(peak - 8f) < 0.1f);
            Check("the release flies along the swing, within 2 deg (got " + degrees(dir, along).ToString("F2") + ")",
                  solved && degrees(dir, along) < 2f);

            // The low arc: a missile launched along the solve lands on the target.
            Vec3 target = new Vec3(20f, 5f, 1.5f);
            object[] bArgs = { target, 25f, 9.81f, Vec3.Zero };
            bool reach = (bool)ballistic.Invoke(null, bArgs);
            Vec3 launch = (Vec3)bArgs[3];
            Vec3 p = Vec3.Zero, v = launch * 25f;
            float flat = (float)Math.Sqrt(target.x * target.x + target.y * target.y);
            for (int k = 0; k < 200000 && (float)Math.Sqrt(p.x * p.x + p.y * p.y) < flat; k++)
            {
                v.z -= 9.81f * 1e-5f;
                p += v * 1e-5f;
            }
            Check("a launch along the ballistic solve lands within 1 cm (missed by "
                  + (p - target).Length.ToString("F3") + " m)", reach && (p - target).Length < 0.01f);
            object[] far = { new Vec3(500f, 0f, 0f), 25f, 9.81f, Vec3.Zero };
            Check("out of reach has no solve", !(bool)ballistic.Invoke(null, far));

            // The assist's bend is capped, and a small miss is corrected fully.
            const float FiveDeg = (float)(5.0 * Math.PI / 180.0);
            Vec3 hand = new Vec3(1f, 0f, 0f);
            Vec3 tenOff = new Vec3((float)Math.Cos(0.1745), (float)Math.Sin(0.1745), 0f);
            Vec3 threeOff = new Vec3((float)Math.Cos(0.0524), 0f, (float)Math.Sin(0.0524));
            Vec3 bent = (Vec3)bend.Invoke(null, new object[] { hand, tenOff, FiveDeg });
            Vec3 full = (Vec3)bend.Invoke(null, new object[] { hand, threeOff, FiveDeg });
            Check("a throw 10 deg off its target is bent only 5 (got " + degrees(hand, bent).ToString("F2") + ")",
                  Math.Abs(degrees(hand, bent) - 5f) < 0.05f && degrees(bent, tenOff) < 5.05f);
            Check("a throw 3 deg off its target goes onto it", degrees(full, threeOff) < 0.01f);

            // The string: never faster than full in 2 s, forward at once.
            float s0 = 0f, maxStep = 0f, atOne = -1f;
            const float Dt = 1f / 90f;
            for (int k = 1; k <= 200; k++)
            {
                float next = (float)span.Invoke(null, new object[] { s0, 1f, Dt, 2f });
                maxStep = Math.Max(maxStep, next - s0);
                s0 = next;
                if (atOne < 0f && s0 >= 1f)
                    atOne = k * Dt;
            }
            Check("the string never rises faster than full-in-2 s", maxStep <= Dt / 2f + 1e-6f);
            Check("a steady full pull latches after 2.0 s (got " + atOne.ToString("F3") + " s)",
                  Math.Abs(atOne - 2f) < 0.02f);
            float half = (float)span.Invoke(null, new object[] { 0.6f, 0.3f, Dt, 2f });
            Check("the string follows the hand forward at once", Near(half, 0.3f));

            // The rifle hold: the right hand stays, the fore-stock aims at the left.
            MatrixFrame frame = MatrixFrame.Identity;
            frame.rotation.RotateAboutUp(0.4f);
            frame.origin = new Vec3(1f, 2f, 1.2f);
            Vec3 foreLocal = new Vec3(0f, 0.02f, 0.5f);
            Vec3 left = new Vec3(1.3f, 2.4f, 1.5f);
            MatrixFrame held = (MatrixFrame)align.Invoke(null,
                new object[] { frame, frame.origin, frame.TransformToParent(foreLocal), left });
            Vec3 fore = held.TransformToParent(foreLocal);
            float off = Vec3.CrossProduct((fore - held.origin).NormalizedCopy(), (left - held.origin).NormalizedCopy()).Length;
            float det = Vec3.DotProduct(Vec3.CrossProduct(held.rotation.s, held.rotation.f), held.rotation.u);
            Check("two hands: the grip stays on the right hand", (held.origin - frame.origin).Length < 1e-4f);
            Check("two hands: the fore-stock lies on the line to the left hand (" + (off * 1000f).ToString("F2")
                  + " mrad)", off < 1e-3f && Vec3.DotProduct(fore - held.origin, left - held.origin) > 0f);
            Check("two hands: still a rotation", Math.Abs(det - 1f) < 1e-3f);

            // A second hand keeps its fitted stock finger hold until grip release.
            MethodInfo stockGrip = xbowType.GetMethod("TryMainGrip", Priv);
            FieldInfo owned = xbowType.GetField("_owned", Priv);
            FieldInfo fitted = xbowType.GetField("_haveMainGrip", Priv);
            FieldInfo holds = xbowType.GetField("_hold", Priv);
            FieldInfo point = xbowType.GetField("_mainGripPoint", Priv);
            bool wasOwned = (bool)owned.GetValue(null), wasFitted = (bool)fitted.GetValue(null);
            bool[] handHolds = (bool[])holds.GetValue(null);
            int mainSide = (int)VrHandSlots.MainController;
            bool wasHolding = handHolds[mainSide];
            Vec3 wasPoint = (Vec3)point.GetValue(null);
            try
            {
                owned.SetValue(null, true);
                fitted.SetValue(null, true);
                handHolds[mainSide] = true;
                point.SetValue(null, new Vec3(0.02f, 0f, 0f));
                object[] gripArgs = { VrHandSlots.MainController, Vec3.Zero, Vec3.Zero, null };
                Check("crossbow second-hand fingers hold the captured stock line",
                    (bool)stockGrip.Invoke(null, gripArgs) && Near(((Vec3)gripArgs[1]).x, 0.02f));
                handHolds[mainSide] = false;
                Check("crossbow second-hand finger hold ends on release",
                    !(bool)stockGrip.Invoke(null, gripArgs));
            }
            finally
            {
                owned.SetValue(null, wasOwned);
                fitted.SetValue(null, wasFitted);
                handHolds[mainSide] = wasHolding;
                point.SetValue(null, wasPoint);
            }

            // TEST 375: the stock's body - beside it is in reach, past its end is not.
            MethodInfo toSegment = xbowType.GetMethod("ToSegment", Priv);
            if (toSegment == null)
            {
                Fail("VrCrossbow.ToSegment not found via reflection");
                return;
            }

            Vec3 butt = new Vec3(0f, 0f, -0.3f), front = new Vec3(0f, 0f, 0.6f);
            object[] beside = { new Vec3(0.05f, 0f, 0.2f), butt, front, 0f };
            object[] past = { new Vec3(0f, 0f, 0.9f), butt, front, 0f };
            float dBeside = (float)toSegment.Invoke(null, beside);
            float dPast = (float)toSegment.Invoke(null, past);
            Check("a hand 5 cm beside the stock is 5 cm off its body, 0.5 m along it",
                  Near(dBeside, 0.05f) && Near((float)beside[3], 0.5f));
            Check("a hand 0.3 m past the stock's front is 0.3 m off it (the end, not the line)",
                  Near(dPast, 0.3f) && Near((float)past[3], 0.9f));

            // TEST 560: the crossbow on a palm. A left hand bone at 1.1x scale,
            // turned: in its own frame the palm faces +y, the fingers run +x, the
            // thumb is on +z. Turned so the palm faces the sky, the crossbow sits
            // upright; its stock runs through the channel toward the thumb.
            MethodInfo palmItem = xbowType.GetMethod("PalmItem", Priv);
            if (palmItem == null)
            {
                Fail("VrCrossbow.PalmItem not found via reflection");
                return;
            }

            // Local +y (the palm) onto world up; s x f = u.
            float c7 = (float)Math.Cos(0.7), s7 = (float)Math.Sin(0.7);
            MatrixFrame bone = new MatrixFrame(
                new Mat3(new Vec3(c7, s7, 0f) * 1.1f, new Vec3(0f, 0f, 1f) * 1.1f, new Vec3(s7, -c7, 0f) * 1.1f),
                new Vec3(3f, -2f, 1.4f));
            Vec3 channel = new Vec3(0.08f, 0.015f, 0f);
            Vec3 palmUp = new Vec3(0f, 1f, 0f), fingersX = new Vec3(1f, 0f, 0f), thumbZ = new Vec3(0f, 0f, 1f);
            MatrixFrame onPalm = (MatrixFrame)palmItem.Invoke(null,
                new object[] { bone, channel, palmUp, fingersX, thumbZ, 0.36f, 0f, 0f });
            Vec3 thumbWorld = bone.rotation.TransformToParent(thumbZ).NormalizedCopy();
            float palmDet = Vec3.DotProduct(Vec3.CrossProduct(onPalm.rotation.s, onPalm.rotation.f), onPalm.rotation.u);
            Check("palm up: the crossbow's top faces the sky (" + degrees(onPalm.rotation.f, Vec3.Up).ToString("F2") + " deg)",
                  degrees(onPalm.rotation.f, Vec3.Up) < 0.5f);
            Check("palm up: a unit right-handed frame through a 1.1x hand (det " + palmDet.ToString("F4") + ")",
                  Math.Abs(palmDet - 1f) < 1e-3f && Math.Abs(onPalm.rotation.u.Length - 1f) < 1e-4f);
            Check("palm up: the muzzle points past the thumb", degrees(onPalm.rotation.u, thumbWorld) < 0.5f);
            Check("palm up: 0.36 m up the stock is the palm channel",
                  (onPalm.TransformToParent(new Vec3(0f, 0f, 0.36f)) - bone.TransformToParent(channel)).Length < 1e-4f);

            MatrixFrame diagonal = (MatrixFrame)palmItem.Invoke(null,
                new object[] { bone, channel, palmUp, fingersX, thumbZ, 0.36f, 0.35f, 0f });
            Check("diagonal: the stock turns 20 deg toward the fingers, the top stays up",
                  Math.Abs(degrees(diagonal.rotation.u, thumbWorld) - 20.05f) < 0.1f
                  && Vec3.DotProduct(diagonal.rotation.u, bone.rotation.TransformToParent(fingersX)) > 0f
                  && degrees(diagonal.rotation.f, Vec3.Up) < 0.5f);
        }

        private static void CrossbowCalibration()
        {
            Section("crossbow calibration survives saving and closing the tool");
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            Type skin = typeof(VrSkinnedWeapon);
            Type heldType = skin.GetNestedType("Held", BindingFlags.NonPublic);
            Type xbow = skin.Assembly.GetType("BannerlordVR.VR.VrCrossbow", true);
            var held = (Array)skin.GetField("_held", flags).GetValue(null);
            var last = (Array)skin.GetField("_toolLast", flags).GetValue(null);
            var poses = (Dictionary<string, float[]>)skin.GetField("_poses", flags).GetValue(null);
            var config = (Dictionary<string, string>)typeof(VrConfig).GetField("Values", flags).GetValue(null);
            var oldConfig = new Dictionary<string, string>(config);
            var oldPoses = new Dictionary<string, float[]>(poses);
            var path = typeof(VrConfig).GetProperty("Path");
            var shipped = typeof(VrConfig).GetField("_shipped", flags);
            string oldPath = VrConfig.Path, oldShipped = (string)shipped.GetValue(null);
            var profile = skin.GetField("_profile", flags);
            object oldProfile = profile.GetValue(null), oldHeld = held.GetValue(0), oldLast = last.GetValue(0);
            var palmEver = xbow.GetField("_palmEver", flags);
            object oldPalmEver = palmEver.GetValue(null);
            string root = Path.Combine(Path.GetTempPath(), "bvr-crossbow-calibration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                path.SetValue(null, Path.Combine(root, "personal.cfg"));
                shipped.SetValue(null, Path.Combine(root, "defaults.cfg"));
                palmEver.SetValue(null, true); // A previous automatic hold must not override the edit or finger fit.
                object h = Activator.CreateInstance(heldType, true);
                heldType.GetField("Class").SetValue(h, "Crossbow");
                heldType.GetField("Ranged").SetValue(h, true);
                heldType.GetField("Frozen").SetValue(h, true);
                heldType.GetField("ItemLocal").SetValue(h, MatrixFrame.Identity);
                ((List<MetaMesh>)heldType.GetField("Copies").GetValue(h)).Add(null);
                held.SetValue(h, 0);
                last.SetValue(h, 0);
                var command = skin.GetMethod("ToolCommand", flags);
                var snapshot = skin.GetMethod("ToolSnapshot", flags);
                float[] edit = { 0.284f, -0.005f, -0.124f, 124.9f, 128.7f, -214f };
                for (int p = 0; p <= 2; p++)
                {
                    profile.SetValue(null, p);
                    foreach (var save in new[] { BvrToolCommand.SaveItem, BvrToolCommand.SaveClass })
                    {
                        heldType.GetField("ItemId").SetValue(h, "calibration_test_crossbow_" + p + "_" + save);
                        command.Invoke(null, new object[] { 0, BvrToolCommand.Adjust, edit });
                        MatrixFrame preview = (MatrixFrame)heldType.GetField("Frame").GetValue(h);
                        string status = (string)command.Invoke(null, new object[] { 0, save, edit });
                        Check("crossbow " + save + " profile " + p + " reports saved", status.StartsWith("Saved"));
                        poses.Clear();
                        config.Clear();
                        typeof(VrConfig).GetMethod("ReadInto", flags).Invoke(null, new object[] { VrConfig.Path });
                        skin.GetMethod("Refresh", flags).Invoke(null, null);
                        Check("crossbow " + save + " profile " + p + " resolves the saved scope",
                            ((string)heldType.GetField("PoseKey").GetValue(h)).StartsWith(
                                save == BvrToolCommand.SaveItem ? "fitem:" : "fclass:"));
                        float[] loaded = new float[6];
                        snapshot.Invoke(null, new object[] { 0, loaded, null });
                        Check("crossbow " + save + " profile " + p + " reloads all six values", Array.TrueForAll(
                            new[] { 0, 1, 2, 3, 4, 5 }, i => Near(loaded[i], edit[i])));
                        MatrixFrame after = (MatrixFrame)heldType.GetField("Frame").GetValue(h);
                        Check("crossbow " + save + " profile " + p + " keeps the preview frame",
                            (preview.origin - after.origin).Length < 1e-5f
                            && (preview.rotation.f - after.rotation.f).Length < 1e-5f
                            && (preview.rotation.u - after.rotation.u).Length < 1e-5f);
                        object[] palmArgs = { HandRole.Main, "Crossbow", MatrixFrame.Identity };
                        Check("crossbow " + save + " profile " + p + " ignores stale palm finger fit",
                            !(bool)xbow.GetMethod("TryPalmLocal", flags).Invoke(null, palmArgs));
                        Check("crossbow " + save + " profile " + p + " disables automatic palm placement",
                            !(bool)xbow.GetProperty("UsePalmPose", flags).GetValue(null));
                    }
                }
                heldType.GetField("PoseKey").SetValue(h, null);
                Check("an uncalibrated crossbow keeps the automatic palm fallback",
                    (bool)xbow.GetProperty("UsePalmPose", flags).GetValue(null));
            }
            finally
            {
                path.SetValue(null, oldPath);
                shipped.SetValue(null, oldShipped);
                profile.SetValue(null, oldProfile);
                held.SetValue(oldHeld, 0);
                last.SetValue(oldLast, 0);
                palmEver.SetValue(null, oldPalmEver);
                poses.Clear();
                foreach (var pair in oldPoses) poses.Add(pair.Key, pair.Value);
                config.Clear();
                foreach (var pair in oldConfig) config.Add(pair.Key, pair.Value);
                Directory.Delete(root, true);
            }
        }

        private static void ReinAppearance()
        {
            Section("static vanilla rein section and curve math");
            Type rope = typeof(VrSkinnedWeapon).Assembly.GetType("BannerlordVR.VR.VrRope", true);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var section = rope.GetMethod("StraightReinSection", flags);
            var vertices = new List<Vec3>();
            foreach (float side in new[] { -1f, 1f })
                for (int i = 0; i <= 50; i++)
                {
                    float y = i / 50f, z = 1.6f - 0.4f * y + 0.2f * y * y;
                    foreach (float x in new[] { -0.014f, 0.014f })
                        foreach (float dz in new[] { -0.003f, 0.003f })
                            vertices.Add(new Vec3(side * 0.22f + x, y, z + dz));
                }
            var piece = (Vec3[])section.Invoke(null, new object[] { vertices.ToArray() });
            Check("the section keeps vertex and UV indices", piece != null && piece.Length == vertices.Count);
            if (piece != null)
            {
                bool uniform = Math.Abs(piece[0].x) >= 0.013f, oppositeCollapsed = true;
                for (int v = 0; v < vertices.Count; v++)
                {
                    if (vertices[v].x < 0f)
                        uniform &= Near(piece[v].x, piece[v % 4].x) && Near(piece[v].z, piece[v % 4].z);
                    else
                        oppositeCollapsed &= piece[v].x == 0f && piece[v].z == 0f;
                }
                Check("the fixture's strip rings have uniform cross-sections", uniform);
                Check("unused geometry collapses to degenerate faces", oppositeCollapsed);
            }
            Check("empty or collapsed geometry uses the fallback",
                section.Invoke(null, new object[] { Array.Empty<Vec3>() }) == null
                && section.Invoke(null, new object[] { new Vec3[12] }) == null);
            var coarse = vertices.FindAll(p => Math.Abs(p.y * 5f - (float)Math.Round(p.y * 5f)) < 1e-4f);
            Check("widely spaced rings can also supply a textured section",
                section.Invoke(null, new object[] { coarse.ToArray() }) != null);
            var curve = rope.GetMethod("CurvePoint", flags);
            Vec3 Sample(Vec3[] points, float at) => (Vec3)curve.Invoke(null, new object[] { points, at });
            Vec3[] line = { Vec3.Zero, new Vec3(0f, 1f, 0f), new Vec3(0f, 2f, 0f) };
            Check("the curve keeps both attachment points and straight spans",
                Sample(line, 0f).Length < 1e-5f && (Sample(line, 2f) - line[2]).Length < 1e-5f
                && (Sample(line, 0.3f) - new Vec3(0f, 0.3f, 0f)).Length < 1e-5f);
            Vec3[] bend = { Vec3.Zero, new Vec3(0f, 0.5f, -0.2f), new Vec3(0f, 1f, 0f), new Vec3(0f, 1.5f, 0.4f) };
            Vec3 left = (Sample(bend, 1f) - Sample(bend, 0.999f)) * 1000f;
            Vec3 right = (Sample(bend, 1.001f) - Sample(bend, 1f)) * 1000f;
            Check("the curve tangent is continuous across physics nodes", (left - right).Length < 0.005f);
            Check("collapsed spans remain finite", Sample(new Vec3[3], 0.5f).Length == 0f);
        }

        // -- TESTs 470-473: the rope's gestures, the rope, the tiller -----------
        private static void RidingAndHelm()
        {
            Section("lead rope, rope physics, tiller (TESTs 470-473)");

            const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            Assembly asm = typeof(VrSkinnedWeapon).Assembly;
            Type rein = asm.GetType("BannerlordVR.VR.VrRein");
            Type rope = asm.GetType("BannerlordVR.VR.VrRope");
            Type helm = asm.GetType("BannerlordVR.VR.VrHelm");
            Type stateType = rein?.GetNestedType("GestureState", BindingFlags.NonPublic);
            MethodInfo classify = rein?.GetMethod("Classify", Any);
            MethodInfo steer = rein?.GetMethod("Steer", Any);
            MethodInfo step = rope?.GetMethod("Step", Any);
            MethodInfo outOf = rope?.GetMethod("OutOfCapsule", Any);
            MethodInfo rudder = helm?.GetMethod("RudderFor", Any);
            if (stateType == null || classify == null || steer == null || step == null || outOf == null || rudder == null)
            {
                Fail("VrRein.Classify/Steer, VrRope.Step/OutOfCapsule or VrHelm.RudderFor not found via reflection");
                return;
            }

            object Fresh()
            {
                object st = Activator.CreateInstance(stateType);
                stateType.GetField("UpAt").SetValue(st, -10f);
                stateType.GetField("StepAt").SetValue(st, -10f);
                return st;
            }

            // Extreme speeds and travels, so any sane cfg override still reads them.
            object s = Fresh();
            string Run(float now, float vUp, float vBack, float back, float lat)
            {
                object[] args = { s, now, vUp, vBack, back, lat };
                string g = classify.Invoke(null, args).ToString();
                s = args[0];
                return g;
            }

            string up = Run(1.00f, 5f, 0f, 0f, 0f);
            string down = Run(1.15f, -5f, 0f, 0f, 0f);
            Check("a flick, up then down, is one gear faster", up == "None" && down == "Faster");
            string again = Run(1.20f, 5f, 0f, 0f, 0f) + Run(1.25f, -5f, 0f, 0f, 0f);
            Check("a second flick inside the cooldown is ignored", again == "NoneNone");
            string later = Run(3.00f, 5f, 0f, 0f, 0f) + Run(3.10f, -5f, 0f, 0f, 0f);
            Check("a second flick after it steps again", later == "NoneFaster");

            s = Fresh();
            Check("a down-stroke alone is not a flick", Run(1f, -5f, 0f, 0f, 0f) == "None");

            s = Fresh();
            string yank = Run(1f, 0f, 8f, 0.3f, 0f);
            string still = Run(1.02f, 0f, 8f, 0.3f, 0f);
            string after = Run(1.05f, 0f, 0f, 0.3f, 0f);
            Check("a yank to the chest is ONE stop, and its own travel is not also a pull",
                  yank == "Stop" && still == "None" && after == "None");

            // TEST 470b: the 09:27 run's misreads.
            s = Fresh();
            string flickUp = Run(1.0f, 5f, 1f, 0f, 0f);
            string flickDown = Run(1.1f, -6f, 3f, 0.15f, 0f);
            Check("a flick whose down-stroke arcs back to the chest is a flick, not a stop",
                  flickUp == "None" && flickDown == "Faster");
            s = Fresh();
            Check("an up-flick with some back speed (gallop, 09:27:34) is not a stop",
                  Run(1f, 4.5f, 2.5f, -0.04f, 0f) == "None");
            s = Fresh();
            Run(1.0f, 5f, 0f, 0f, 0f);
            string flick2 = Run(1.1f, -5f, 0f, 0.2f, 0f);
            string rest1 = Run(1.2f, 0f, 0f, 0.2f, 0f) + Run(1.6f, 0f, 0f, 0.2f, 0f);
            string rest2 = Run(2.0f, 0f, 0f, 0.2f, 0f);
            Check("a flick that ends with the hand back is not undone by a pull at once",
                  flick2 == "Faster" && rest1 == "NoneNone" && rest2 == "Slower");

            s = Fresh();
            string entry = Run(1f, 0f, 0f, 0.3f, 0f);
            string hold = Run(1.5f, 0f, 0f, 0.3f, 0f);
            string held = Run(1.6f, 0f, 0f, 0.3f, 0f);
            string repeat = Run(6f, 0f, 0f, 0.3f, 0f);
            Check("a centred pull steps down once it is held, then again only after the repeat",
                  entry == "None" && hold == "Slower" && held == "None" && repeat == "Slower");
            s = Fresh();
            Check("a hand only passing through the pull zone is not a pull",
                  Run(1f, 0f, 1.5f, 0.3f, 0f) + Run(1.5f, 0f, 1.5f, 0.3f, 0f) == "NoneNone");

            s = Fresh();
            Check("a pull far to the side is a steer, not a brake", Run(1f, 0f, 0f, 0.3f, 0.5f) == "None");

            // Dead 0.04, full 0.20 (a throw of 0.16): knees at 0.4 (0.104) and 0.6 (0.136).
            float St(float lat) => (float)steer.Invoke(null, new object[] { lat, 0.04f, 0.20f, 0.4f, 0.6f,
                                                                            0.12f, 0.12f, 0.25f, 0.25f });
            Check("inside the dead band the rope does not steer", St(0.02f) == 0f && St(-0.03f) == 0f);
            Check("TEST 470j: 0-40% is flat very slow",
                  Math.Abs(St(0.0401f) - 0.12f) < 1e-3f && Math.Abs(St(-0.09f) + 0.12f) < 1e-3f);
            Check("TEST 470j: 40-60% is flat slow", Math.Abs(St(0.11f) - 0.25f) < 1e-3f && Math.Abs(St(0.13f) - 0.25f) < 1e-3f);
            Approx("TEST 470j: past 60% is full", St(0.14f), 1f);
            float ramp = (float)steer.Invoke(null, new object[] { 0.072f, 0.04f, 0.20f, 0.4f, 0.6f, 0.1f, 0.3f, 0.3f, 0.6f });
            Check("TEST 470j: a band with different ends ramps (halfway through band 1)", Math.Abs(ramp - 0.2f) < 0.01f);
            Approx("past full stays full right", St(0.5f), 1f);

            MethodInfo duty = rein.GetMethod("Duty", Any);
            if (duty == null)
                Fail("VrRein.Duty not found via reflection");
            else
            {
                object[] args = { 0f, 0.3f };
                int on = 0;
                for (int k = 0; k < 100; k++)
                {
                    if ((bool)duty.Invoke(null, args))
                        on++;
                }
                Check("TEST 470c: a 0.3 steer turns on 30 of 100 ticks (got " + on + ")", on == 30);
            }

            MethodInfo sane = rope.GetMethod("Sane", Any);
            if (sane == null)
                Fail("VrRope.Sane not found via reflection");
            else
            {
                MatrixFrame good = MatrixFrame.Identity;
                good.origin = new Vec3(0.1f, 1.2f, 1.5f);
                MatrixFrame zero = default;
                MatrixFrame wild = MatrixFrame.Identity;
                wild.origin = new Vec3(6.36e29f, 0f, 0f);
                Check("TEST 471e: a bone frame is sane; an unwritten or 1e29 m one is not",
                      (bool)sane.Invoke(null, new object[] { good }) && !(bool)sane.Invoke(null, new object[] { zero })
                      && !(bool)sane.Invoke(null, new object[] { wild }));
            }

            MethodInfo ends = rope.GetMethod("CapsuleEnds", Any);
            if (ends == null)
                Fail("VrRope.CapsuleEnds not found via reflection");
            else
            {
                // cloth_bodies.xml 'horse_neck_head_rein', "New capsule4".
                float[] frame = { -0.000067f, 0.546616f, 0.837384f, 0f, -0.000017f, 0.837384f, -0.546616f, 0f,
                                  -1f, -0.000051f, -0.000047f, 0f, -0.004315f, 0.831879f, 1.307074f, 1f };
                object[] args = { frame, Vec3.Zero, 0.73f, null, null };
                ends.Invoke(null, args);
                Vec3 e0 = (Vec3)args[3], e1 = (Vec3)args[4];
                Check("TEST 471d: a vanilla capsule is length long along its frame's first axis",
                      Math.Abs((e1 - e0).Length - 0.73f) < 1e-3f && (e1 - e0).z > 0.5f);
                Expect("TEST 471d: centred on its frame's origin", (e0 + e1) * 0.5f, new Vec3(-0.004315f, 0.831879f, 1.307074f));
            }

            // A rope hung between two pins 1 m apart, 1.4 m long, at 90 Hz.
            const int n = 16;
            float seg = 1.4f / (n - 1);
            var p = new Vec3[n];
            var prev = new Vec3[n];
            var pinned = new bool[n];
            for (int i = 0; i < n; i++)
                p[i] = prev[i] = new Vec3(i / (float)(n - 1), 0f, 0f);
            pinned[0] = pinned[n - 1] = true;
            Vec3 far = new Vec3(100f, 100f, 100f);
            for (int k = 0; k < 300; k++)
                step.Invoke(null, new object[] { p, prev, pinned, new Vec3(0f, 0f, -9.81f), 1f / 90f, seg,
                                                 new[] { far }, new[] { far + new Vec3(0f, 0f, 1f) },
                                                 new[] { 0.1f }, new[] { 0.1f }, 1, 12 });

            float worst = 0f;
            for (int i = 0; i + 1 < n; i++)
                worst = Math.Max(worst, Math.Abs((p[i + 1] - p[i]).Length - seg) / seg);
            Check("a hanging rope keeps its length within 2 % (worst " + (worst * 100f).ToString("F2") + " %)",
                  worst < 0.02f);
            Check("it sags below its pins", p[n / 2].z < -0.2f);
            Check("the pins never move", p[0].x == 0f && p[0].z == 0f && Near(p[n - 1].x, 1f) && p[n - 1].z == 0f);

            // TEST 471c: a rein draped over a neck capsule lies on it, not through it.
            for (int i = 0; i < n; i++)
                p[i] = prev[i] = new Vec3(i / (float)(n - 1), 0f, 0.3f);
            Vec3 neckA = new Vec3(0.5f, -1f, 0f), neckB = new Vec3(0.5f, 1f, 0f);
            for (int k = 0; k < 300; k++)
                step.Invoke(null, new object[] { p, prev, pinned, new Vec3(0f, 0f, -9.81f), 1f / 90f, seg,
                                                 new[] { far, neckA }, new[] { far, neckB },
                                                 new[] { 0.1f, 0.25f }, new[] { 0.1f, 0.25f }, 2, 12 });
            float inside = 0f;
            for (int i = 0; i < n; i++)
            {
                float d = (float)Math.Sqrt((p[i].x - 0.5f) * (p[i].x - 0.5f) + p[i].z * p[i].z);
                inside = Math.Max(inside, 0.25f - d);
            }
            Check("a rein draped over the neck never sinks into it (worst " + (inside * 100f).ToString("F1") + " cm)",
                  inside < 0.01f);

            Vec3 a = Vec3.Zero, b = new Vec3(0f, 1f, 0f);
            Vec3 pushed = (Vec3)outOf.Invoke(null, new object[] { new Vec3(0.05f, 0.5f, 0f), a, b, 0.24f, 0.12f });
            Approx("a point inside the tapered neck lands on its surface (0.18 halfway)", pushed.x, 0.18f);
            Vec3 clear = (Vec3)outOf.Invoke(null, new object[] { new Vec3(0.3f, 0.5f, 0f), a, b, 0.24f, 0.12f });
            Check("a point outside the neck is left alone", Near(clear.x, 0.3f));

            bool round = true;
            foreach (float f in new[] { -1f, -0.5f, -0.1f, 0f, 0.3f, 1f })
            {
                float r = (float)rudder.Invoke(null, new object[] { f });
                round &= Math.Abs(Math.Sign(r) * r * r - f) < 1e-4f;
            }
            Check("the tiller's root survives the engine's square: the hand is linear", round);
        }

        // -- TEST 481: the five-finger rig on a synthetic hand ------------------
        private static void FingerRig()
        {
            Section("TEST 481 finger rig (hand rest axes: x fingers, y palm, z across)");

            // A palm box, four finger tubes (index at +z), a thumb lump on the
            // palm side - the layout TEST 480 measured on the game's gauntlets.
            var pts = new System.Collections.Generic.List<Vec3>();
            for (float x = 0f; x <= 0.1f; x += 0.005f)
                for (float z = -0.065f; z <= 0.0651f; z += 0.005f)
                {
                    pts.Add(new Vec3(x, -0.04f, z, -1f));
                    pts.Add(new Vec3(x, 0.01f, z, -1f));
                }

            float[] fz = { -0.05f, -0.017f, 0.016f, 0.05f };
            float[] tip = { 0.17f, 0.19f, 0.205f, 0.187f };
            for (int f = 0; f < 4; f++)
                for (float x = 0.1f; x <= tip[f]; x += 0.004f)
                    for (int a = 0; a < 8; a++)
                    {
                        double t = a * Math.PI / 4;
                        pts.Add(new Vec3(x, -0.012f + 0.008f * (float)Math.Cos(t), fz[f] + 0.008f * (float)Math.Sin(t), -1f));
                    }

            Vec3 t0 = new Vec3(0.05f, 0.02f, 0.065f, -1f), t1 = new Vec3(0.11f, 0.035f, 0.075f, -1f);
            for (float u = 0f; u <= 1.0001f; u += 0.0667f)
                for (int a = 0; a < 8; a++)
                {
                    double t = a * Math.PI / 4;
                    Vec3 c = t0 + (t1 - t0) * u;
                    pts.Add(new Vec3(c.x, c.y + 0.009f * (float)Math.Cos(t), c.z + 0.009f * (float)Math.Sin(t), -1f));
                }

            VrFingerRig.Fit fit = VrFingerRig.FitHand(pts, 0.09f, 0.012f);
            Check("the rig fits (" + fit.Why + ")", fit.Valid);
            if (!fit.Valid)
                return;

            Check("the thumb is found, on +z", fit.HasThumb && fit.ThumbSide > 0f);
            Check("the palm side is +y (the thumb lump's side)", fit.PalmSign > 0f);
            Check("index ~8.4 cm from its MCP (got " + fit.Length[1].ToString("F3") + ")", Math.Abs(fit.Length[1] - 0.084f) < 0.01f);
            Check("middle is the longest finger", fit.Length[2] > fit.Length[1] && fit.Length[2] > fit.Length[3] && fit.Length[2] > fit.Length[4]);

            bool bounded = true;
            foreach (Vec3 v in pts)
            {
                VrFingerRig.Weigh(fit, v, out Vec3 w);
                float sum = w.x + w.y + w.z;
                bounded &= w.x >= 0f && w.y >= 0f && w.z >= 0f && sum <= 1.0001f;
            }

            Check("every weight is in [0, 1] and sums to at most 1", bounded);
            Check("a palm point moves with no finger", VrFingerRig.Weigh(fit, new Vec3(0.03f, 0.01f, 0f, -1f), out _) < 0);

            var middleTip = new Vec3(0.2f, -0.004f, 0.016f, -1f);
            int digit = VrFingerRig.Weigh(fit, middleTip, out Vec3 wt);
            Check("the middle fingertip rides the middle finger's distal bone", digit == 2 && wt.z > 0.9f);
            Check("the thumb tip rides the thumb", VrFingerRig.Weigh(fit, t1, out _) == 0);

            // 492: the thumb's index-facing side, 7.5 mm from the index column near its
            // base - 490's "8 mm past the index" cut left it at bind (the flap at the web).
            Vec3 thumbInner = t0 + (t1 - t0) * 0.15f + new Vec3(0f, 0f, -0.009f, -1f);
            Check("the thumb's index-facing side rides the thumb", VrFingerRig.Weigh(fit, thumbInner, out _) == 0);
            bool indexKept = true;
            for (float x = 0.1f; x <= tip[3]; x += 0.004f)
                for (int a = 0; a < 8; a++)
                {
                    double t = a * Math.PI / 4;
                    var p = new Vec3(x, -0.012f + 0.008f * (float)Math.Cos(t), fz[3] + 0.008f * (float)Math.Sin(t), -1f);
                    indexKept &= VrFingerRig.Weigh(fit, p, out _) != 0;
                }

            Check("the thumb takes no point of the index beside it", indexKept);

            // 497: fingers slid along a handle across them (z) close on the middle
            // finger until they touch - the synthetic tubes (8 mm) stand 33-34 mm apart.
            var zero = new Vec3[VrFingerRig.Digits];
            var radii = new float[VrFingerRig.Digits][];
            for (int f = 0; f < VrFingerRig.Digits; f++)
                radii[f] = new[] { 0.008f, 0.008f, 0.008f, 0.008f };
            Vec3[] slid = VrFingerRig.SideBySide(fit, zero, zero, radii, new Vec3(0f, 0f, 1f, -1f), 0.001f, 0.05f);
            float zi = fz[3] + slid[1].z, zm = fz[2], zr = fz[1] + slid[3].z, zp = fz[0] + slid[4].z;
            Check("side by side: the index and ring close on the middle finger, the pinky on the ring (gaps "
                  + ((zi - zm - 0.016f) * 1000f).ToString("F1") + "/" + ((zm - zr - 0.016f) * 1000f).ToString("F1") + "/"
                  + ((zr - zp - 0.016f) * 1000f).ToString("F1") + " mm)",
                  slid[1].z < 0f && slid[3].z > 0f && slid[4].z > 0f && slid[2].Length == 0f
                  && Math.Abs(zi - zm - 0.017f) < 0.0015f && Math.Abs(zm - zr - 0.017f) < 0.0015f
                  && Math.Abs(zr - zp - 0.017f) < 0.0015f);
            Check("side by side: no finger slides further than allowed",
                  VrFingerRig.SideBySide(fit, zero, zero, radii, new Vec3(0f, 0f, 1f, -1f), 0.001f, 0.01f)[4].Length <= 0.0101f);

            Expect("zero angles leave a point where it is", VrFingerRig.Bend(fit, 2, Vec3.Zero, middleTip, wt), middleTip);

            Vec3 relax = new Vec3(8f, 12f, 8f, -1f), full = new Vec3(85f, 100f, 70f, -1f);
            Vec3 half = VrFingerRig.Bend(fit, 2, VrFingerRig.Angles(relax, full, 0.5f), middleTip, wt);
            Check("half a curl swings the tip toward the palm (y +" + (half.y - middleTip.y).ToString("F3") + ")",
                  half.y > middleTip.y + 0.04f);
            Vec3 fist = VrFingerRig.Angles(relax, full, 1f);
            Vec3 bent = VrFingerRig.Bend(fit, 2, fist, middleTip, wt);
            Check("a fist tucks the tip back under the palm (x " + (bent.x - middleTip.x).ToString("F3") + ")",
                  bent.y > middleTip.y && bent.x < middleTip.x - 0.08f);

            // 489: the weight ramps across the MCP, so the knuckle rounds over.
            Vec3 mcp = fit.Joint[2, 0];
            VrFingerRig.Weigh(fit, new Vec3(mcp.x + 0.006f, -0.004f, 0.016f, -1f), out Vec3 atKnuckle);
            Check("over the knuckle a finger point is partly weighted (" + (atKnuckle.x + atKnuckle.y + atKnuckle.z).ToString("F2") + ")",
                  atKnuckle.x + atKnuckle.y + atKnuckle.z > 0.2f && atKnuckle.x + atKnuckle.y + atKnuckle.z < 0.8f);
            VrFingerRig.Weigh(fit, new Vec3(fit.KnuckleX + 0.03f, -0.004f, 0.016f, -1f), out Vec3 past);
            Check("2-3 cm past the web it rides its finger fully", past.x + past.y + past.z > 0.99f);

            // 489: a real hand's proportions - the pinky's knuckle furthest back,
            // the middle finger's end segment the longest, the pinky's the shortest.
            float End(int f) => fit.Length[f] - Vec3.DotProduct(fit.Joint[f, 2] - fit.Joint[f, 0], fit.Dir[f]);
            Check("the knuckles arc back: the pinky's and ring's behind the middle's (MCP x " + fit.Joint[4, 0].x.ToString("F3") + " / "
                  + fit.Joint[3, 0].x.ToString("F3") + " / " + fit.Joint[2, 0].x.ToString("F3") + ")",
                  fit.Joint[4, 0].x < fit.Joint[2, 0].x - 0.005f && fit.Joint[3, 0].x <= fit.Joint[2, 0].x);
            Check("end segments: middle longest, pinky shortest (" + (End(1) * 1000f).ToString("F0") + "/" + (End(2) * 1000f).ToString("F0")
                  + "/" + (End(3) * 1000f).ToString("F0") + "/" + (End(4) * 1000f).ToString("F0") + " mm)",
                  End(2) >= End(1) && End(2) >= End(3) && End(4) <= End(1) && End(4) <= End(3));
            Check("a finger never shrinks: the index still reaches its tip", fit.Length[1] >= 0.083f);

            // 489: the thumb's weight ramps over its base, so the palm stays joined to it.
            float thumbLeast = 1f;
            foreach (Vec3 v in pts)
            {
                if (VrFingerRig.Weigh(fit, v, out Vec3 w) == 0)
                    thumbLeast = Math.Min(thumbLeast, w.x + w.y + w.z);
            }

            VrFingerRig.Weigh(fit, t1, out Vec3 wtip);
            Check("the thumb's base is partly weighted, its tip fully (least " + thumbLeast.ToString("F2") + ", tip "
                  + (wtip.x + wtip.y + wtip.z).ToString("F2") + ")",
                  thumbLeast < 0.8f && wtip.x + wtip.y + wtip.z > 0.99f);

            var knuckle = new Vec3(fit.KnuckleX - 0.02f, -0.012f, 0.016f, -1f);
            VrFingerRig.Weigh(fit, knuckle, out Vec3 wk);
            Check("behind the knuckle the fist moves nothing", (VrFingerRig.Bend(fit, 2, fist, knuckle, wk) - knuckle).Length < 0.001f);

            // TEST 483: a handle held across the palm (along z, on the palm side
            // just behind the knuckles) - each finger stops where it touches it.
            System.Collections.Generic.List<Vec3> Rod(float radius, float y)
            {
                var rod = new System.Collections.Generic.List<Vec3>();
                for (float z = -0.08f; z <= 0.0801f; z += 0.005f)
                    for (int a = 0; a < 12; a++)
                    {
                        double t = a * Math.PI / 6;
                        rod.Add(new Vec3(0.105f + radius * (float)Math.Cos(t), y + radius * (float)Math.Sin(t), z, -1f));
                    }
                return rod;
            }

            var channel = new Vec3(0.105f, 0.026f, 0f, -1f);
            VrFingerRig.Handle h = VrFingerRig.FitHandle(Rod(0.016f, 0.026f), channel, 0.07f);
            Check("a rod in the palm fits as a handle (" + h.Why + ")", h.Valid);
            if (h.Valid)
            {
                Check("its radius is the rod's, 16 mm (got " + (h.Radius * 1000f).ToString("F1") + ")", Math.Abs(h.Radius - 0.016f) < 0.002f);
                Check("its axis runs across the palm (z)", Math.Abs(h.Axis.z) > 0.99f);

                var fp = new System.Collections.Generic.List<Vec3>();
                var fw = new System.Collections.Generic.List<Vec3>();
                foreach (Vec3 v in pts)
                {
                    if (VrFingerRig.Weigh(fit, v, out Vec3 w) == 2 && w.x + w.y + w.z >= 0.3f)
                    {
                        fp.Add(v);
                        fw.Add(w);
                    }
                }

                const float deg = (float)(180.0 / Math.PI);
                Vec3 a = VrFingerRig.Wrap(fit, 2, relax, full, fp, fw, h, 0.002f);
                Vec3 fistRad = full * (1f / deg);
                Check("the middle finger's base stops on the rod, short of a fist (" + (a.x * deg).ToString("F0") + " deg)",
                      a.x > relax.x / deg && a.x < fistRad.x - 0.05f);

                float c1 = 0f;
                while (c1 < 1f && VrFingerRig.Angles(relax, full, c1 + 0.01f).x <= a.x)
                    c1 += 0.01f;
                Vec3 once = VrFingerRig.Angles(relax, full, c1);
                Check("then the middle and end joints wrap on past the first touch (" + (a.y * deg).ToString("F0") + "/"
                      + (a.z * deg).ToString("F0") + " vs " + (once.y * deg).ToString("F0") + "/" + (once.z * deg).ToString("F0") + " deg)",
                      a.y + a.z > once.y + once.z + 0.05f);

                float inside = float.MaxValue;
                for (int i = 0; i < fp.Count; i++)
                {
                    Vec3 d = VrFingerRig.Bend(fit, 2, a, fp[i], fw[i]) - h.Center;
                    float t = Vec3.DotProduct(d, h.Axis);
                    inside = Math.Min(inside, (d - h.Axis * t).Length);
                }

                Check("wrapped, no finger point sinks more than 3 mm into the rod - flesh pressing (closest "
                      + (inside * 1000f).ToString("F1") + " mm from its axis)", inside >= 0.016f - 0.004f);

                // 504: a fingertip cannot curl past its middle joint (491 took it to the fist: 65/36/70).
                Check("the end joint closes no further than the middle one (" + (a.y * deg).ToString("F0") + "/"
                      + (a.z * deg).ToString("F0") + " deg)", a.z <= a.y + 1e-4f);

                // 505: the arrow rests against the rod on the back of the hand's side
                // (left for a bow in the left hand), up it toward the index.
                Vec3 arrow = VrFingerRig.ArrowRest(fit, h, new Vec3[5], new Vec3[5], new Vec3[5], pts, 0.004f);
                float back = Vec3.DotProduct(arrow - h.Center, new Vec3(0f, -fit.PalmSign, 0f, -1f));
                Check("505: the arrow rests on the back of the hand's side of the stave (" + (back * 1000f).ToString("F0")
                      + " mm out, stave 16), toward the index (z " + arrow.z.ToString("F3") + ")",
                      back >= 0.016f && arrow.z * fit.ThumbSide >= h.Center.z * fit.ThumbSide);

                // 506: vertex spacing - seams (repeated spots) are not neighbours.
                var grid = new System.Collections.Generic.List<Vec3>();
                for (int i = 0; i < 10; i++)
                    for (int j = 0; j < 10; j++)
                    {
                        grid.Add(new Vec3(i * 0.01f, j * 0.01f, 0f, -1f));
                        grid.Add(new Vec3(i * 0.01f, j * 0.01f, 0f, -1f));
                    }
                Check("506: a 1 cm grid with every vertex doubled is 1 cm apart (" + (VrFingerRig.Spacing(grid) * 1000f).ToString("F1") + " mm)",
                      Math.Abs(VrFingerRig.Spacing(grid) - 0.01f) < 1e-4f);

                // 504: the measured widths of the synthetic 8 mm tubes.
                float[] wm = VrFingerRig.Widths(fit, 2, fp, fw);
                Check("a finger's width is measured along it (" + string.Join("/", System.Array.ConvertAll(wm, x => (x * 1000f).ToString("F1")))
                      + " mm, tube 8)", System.Array.TrueForAll(wm, x => Math.Abs(x - 0.008f) < 0.0025f));

                VrFingerRig.Handle thick = VrFingerRig.FitHandle(Rod(0.024f, 0.026f), channel, 0.07f);
                Vec3 at = VrFingerRig.Wrap(fit, 2, relax, full, fp, fw, thick, 0.002f);
                Check("a thicker rod stops the finger's base sooner (" + (at.x * deg).ToString("F0") + " < " + (a.x * deg).ToString("F0") + " deg)",
                      thick.Valid && at.x < a.x);

                // 489: a grip lying across the finger roots (487's sword stopped the
                // index unbent, 8/12/8): the points already inside it do not stop the curl.
                VrFingerRig.Handle roots = VrFingerRig.LineHandle(new Vec3(fit.Joint[2, 0].x + 0.012f, -0.004f, 0f, -1f),
                                                                  new Vec3(0f, 0f, 1f, -1f), channel, 0.016f, 0.07f);
                Vec3 ar = VrFingerRig.Wrap(fit, 2, relax, full, fp, fw, roots, 0.002f);
                Check("a grip across the finger roots still lets the finger wrap (" + (ar.x * deg).ToString("F0") + "/"
                      + (ar.y * deg).ToString("F0") + "/" + (ar.z * deg).ToString("F0") + " deg)",
                      ar.x + ar.y + ar.z > (relax.x + relax.y + relax.z) / deg + 1f);
            }

            var board = new System.Collections.Generic.List<Vec3>();
            for (float x = 0.04f; x <= 0.17f; x += 0.01f)
                for (float z = -0.07f; z <= 0.07f; z += 0.01f)
                    board.Add(new Vec3(x, 0.03f, z, -1f));
            Check("a flat board (a shield) is not taken for a handle", !VrFingerRig.FitHandle(board, channel, 0.07f).Valid);

            // 491: a handle sunk into the palm and in front of the knuckles is seated
            // on the palm, behind them; one already there stays put.
            {
                var palmPts = new System.Collections.Generic.List<Vec3>();
                foreach (Vec3 v in pts)
                {
                    if (VrFingerRig.Weigh(fit, v, out _) < 0)
                        palmPts.Add(v);
                }

                Vec3 normal = new Vec3(0f, fit.PalmSign, 0f, -1f);
                VrFingerRig.Handle sunk = VrFingerRig.LineHandle(new Vec3(fit.Joint[2, 0].x, 0.0f, 0f, -1f), new Vec3(0f, 0f, 1f, -1f),
                                                                 channel, 0.016f, 0.07f);
                Vec3 move = VrFingerRig.Seat(palmPts, sunk, normal, fit.Joint[2, 0], fit.Dir[2], 0.06f);
                Vec3 seated = sunk.Center + move;
                Check("a sunk handle is seated on the palm (bottom " + ((seated.y - 0.016f) * 1000f).ToString("F0")
                      + " mm, palm top 10 mm)", Math.Abs(seated.y - 0.016f - 0.011f) < 0.003f);
                Check("and behind the knuckles (front " + ((seated.x + 0.016f) * 1000f).ToString("F0") + " mm, MCP "
                      + (fit.Joint[2, 0].x * 1000f).ToString("F0") + " mm)", seated.x + 0.016f <= fit.Joint[2, 0].x + 0.0025f);

                VrFingerRig.Handle there = VrFingerRig.LineHandle(new Vec3(fit.Joint[2, 0].x - 0.02f, 0.03f, 0f, -1f),
                                                                  new Vec3(0f, 0f, 1f, -1f), channel, 0.016f, 0.07f);
                Check("a handle already resting on the palm behind the knuckles stays (moved "
                      + (VrFingerRig.Seat(palmPts, there, normal, fit.Joint[2, 0], fit.Dir[2], 0.06f).Length * 1000f).ToString("F1") + " mm)",
                      VrFingerRig.Seat(palmPts, there, normal, fit.Joint[2, 0], fit.Dir[2], 0.06f).Length < 0.002f);

                // 491: a thumb lying where the handle is lifts over it, clear of it.
                var tp = new System.Collections.Generic.List<Vec3>();
                var tw = new System.Collections.Generic.List<Vec3>();
                foreach (Vec3 v in pts)
                {
                    if (VrFingerRig.Weigh(fit, v, out Vec3 w) == 0 && w.x + w.y + w.z >= 0.3f)
                    {
                        tp.Add(v);
                        tw.Add(w);
                    }
                }

                VrFingerRig.Handle underThumb = VrFingerRig.LineHandle(new Vec3(0.085f, 0.015f, 0.07f, -1f), new Vec3(0f, 0f, 1f, -1f),
                                                                       channel, 0.012f, 0.07f);
                Vec3 lift = VrFingerRig.Lift(fit, 0, new Vec3(0f, 5f, 5f, -1f), new Vec3(35f, 45f, 60f, -1f), tp, tw, underThumb, 0f);
                bool clear = true;
                float rise = 0f;
                for (int i = 0; i < tp.Count; i++)
                {
                    Vec3 b = VrFingerRig.Bend(fit, 0, VrFingerRig.Angles(new Vec3(0f, 5f, 5f, -1f), new Vec3(35f, 45f, 60f, -1f), 0f), lift, tp[i], tw[i]);
                    Vec3 d = b - underThumb.Center;
                    float t = Vec3.DotProduct(d, underThumb.Axis);
                    clear &= Math.Abs(t) > underThumb.Half || (d - underThumb.Axis * t).Length >= 0.012f;
                    rise = Math.Max(rise, (b - tp[i]).y * fit.PalmSign);
                }

                Check("a thumb in the handle's way lifts over it (" + (lift.Length * 57.3f).ToString("F0") + " deg, up "
                      + (rise * 1000f).ToString("F0") + " mm), clear of it", lift.Length > 0.01f && clear && rise > 0f);
                Check("504: the lift is at most " + VrFingerRig.LiftMaxDeg + " deg, about an axis square to the palm normal",
                      lift.Length <= VrFingerRig.LiftMaxDeg / 57.29f + 1e-4f
                      && Math.Abs(Vec3.DotProduct(lift.NormalizedCopy(), new Vec3(0f, fit.PalmSign, 0f, -1f))) < 0.05f);

                // 504: a wall of points in the thumb's way (the wrapped fingers) stops it sooner.
                VrFingerRig.Handle away = VrFingerRig.LineHandle(new Vec3(0.3f, 0.3f, 0.3f, -1f), new Vec3(0f, 0f, 1f, -1f), channel, 0.01f, 0.01f);
                Vec3 thumbRelax = new Vec3(0f, 5f, 5f, -1f), thumbFist = new Vec3(35f, 45f, 60f, -1f);
                Vec3 free = VrFingerRig.Wrap(fit, 0, thumbRelax, thumbFist, tp, tw, away, 0f, Vec3.Zero);
                var wall = new System.Collections.Generic.List<Vec3>();
                for (int i = 0; i < tp.Count; i++)
                {
                    Vec3 halfWay = VrFingerRig.Bend(fit, 0, VrFingerRig.Angles(thumbRelax, thumbFist, 0.5f), Vec3.Zero, tp[i], tw[i]);
                    if (tw[i].x + tw[i].y + tw[i].z > 0.9f)
                        wall.Add(halfWay + new Vec3(0f, 0f, -0.004f, -1f));
                }
                var block = new VrFingerRig.Obstacle(wall);
                Vec3 stopped = VrFingerRig.Wrap(fit, 0, thumbRelax, thumbFist, tp, tw, away.With(block), 0f, Vec3.Zero);
                Check("504: alone the thumb closes to its fist; a wall half way stops it (" + (free.x * 57.3f).ToString("F0") + "/"
                      + (free.y * 57.3f).ToString("F0") + " -> " + (stopped.x * 57.3f).ToString("F0") + "/" + (stopped.y * 57.3f).ToString("F0") + " deg)",
                      free.x + free.y > stopped.x + stopped.y + 0.1f && block.Count > 0);
            }

            Mitten();

            Section("TESTs 482/487/488 finger hands: line handles, a real close, keys, touch");

            // 487: a crafted haft is a line. 483b's two-handed off hand had its
            // handle centred 0.381 m up the shaft and closed into a fist in the air.
            VrFingerRig.Handle line = VrFingerRig.LineHandle(new Vec3(0.105f, 0.026f, 0.38f, -1f), new Vec3(0f, 0f, 2f, -1f),
                                                             channel, 0.016f, 0.07f);
            Check("a line handle centres on the palm, not 0.38 m up the shaft (centre z "
                  + line.Center.z.ToString("F3") + ")", line.Valid && Math.Abs(line.Center.z) < 1e-4f
                  && Math.Abs(line.Center.x - 0.105f) < 1e-4f && Math.Abs(line.Axis.z - 1f) < 1e-4f);
            {
                var fp = new System.Collections.Generic.List<Vec3>();
                var fw = new System.Collections.Generic.List<Vec3>();
                foreach (Vec3 v in pts)
                {
                    if (VrFingerRig.Weigh(fit, v, out Vec3 w) == 2 && w.x + w.y + w.z >= 0.3f)
                    {
                        fp.Add(v);
                        fw.Add(w);
                    }
                }

                const float deg = (float)(180.0 / Math.PI);
                Vec3 onLine = VrFingerRig.Wrap(fit, 2, relax, full, fp, fw, line, 0.002f);
                Check("on that line the middle finger stops short of a fist (" + (onLine.x * deg).ToString("F0") + " deg)",
                      onLine.x < full.x / deg - 0.05f);
                Check("a line handle with no axis is no handle",
                      !VrFingerRig.LineHandle(Vec3.Zero, Vec3.Zero, channel, 0.016f, 0.07f).Valid);
            }

            // 488: the synthetic fingers are parallel - no splay to take back.
            Check("parallel fingers measure no splay (index " + (fit.Splay[1] * 57.3f).ToString("F1") + " deg)",
                  Math.Abs(fit.Splay[1]) < 0.035f && Math.Abs(fit.Splay[4]) < 0.035f);
            Expect("zero angles and zero spread leave a point where it is",
                   VrFingerRig.Bend(fit, 2, Vec3.Zero, 0f, middleTip, wt), middleTip);

            // 488: an index fanned 11 deg toward the thumb (+z) comes back toward
            // the middle finger as the hand closes; the palm stays put.
            fit.Splay[1] = -0.2f;
            var indexTip = new Vec3(0.165f, -0.004f, 0.05f, -1f);
            VrFingerRig.Weigh(fit, indexTip, out Vec3 wi);
            float spread = VrFingerRig.Spread(fit, 1, 0.4f, 0.9f, 1f);
            Vec3 together = VrFingerRig.Bend(fit, 1, Vec3.Zero, spread, indexTip, wi);
            Check("adduction turns the index tip toward the middle finger (z " + indexTip.z.ToString("F3") + " -> "
                  + together.z.ToString("F3") + ")", spread > 0f && together.z < indexTip.z - 0.005f);
            var palmPoint = new Vec3(0.03f, 0.01f, 0.03f, -1f);
            VrFingerRig.Weigh(fit, palmPoint, out Vec3 wp);
            Expect("adduction leaves the palm", VrFingerRig.Bend(fit, 1, Vec3.Zero, spread, palmPoint, wp), palmPoint);
            Check("the spread is less when open than in a fist",
                  Math.Abs(VrFingerRig.Spread(fit, 1, 0.4f, 0.9f, 0f)) < Math.Abs(spread));
            fit.Splay[1] = 0f;

            // 488: converging - a curled pinky (at -z, the thumb at +z) leans toward the thumb.
            VrFingerRig.Fit lean = VrFingerRig.FitHand(pts, 0.09f, 0.012f, new[] { 0f, 0f, 0f, 10f });
            var pinkyTip = new Vec3(0.165f, -0.004f, -0.05f, -1f);
            VrFingerRig.Weigh(fit, pinkyTip, out Vec3 wpk);
            VrFingerRig.Weigh(lean, pinkyTip, out Vec3 wpl);
            Vec3 plainFist = VrFingerRig.Bend(fit, 4, fist, pinkyTip, wpk);
            Vec3 leanFist = VrFingerRig.Bend(lean, 4, fist, pinkyTip, wpl);
            Check("a converging pinky's fist leans toward the thumb (z " + plainFist.z.ToString("F3") + " -> "
                  + leanFist.z.ToString("F3") + ")", lean.Valid && leanFist.z > plainFist.z + 0.003f);
            Expect("converging leaves the open pinky where it is", VrFingerRig.Bend(lean, 4, Vec3.Zero, pinkyTip, wpl), pinkyTip);

            // 488: keys with hysteresis - a grip held on a boundary does not flicker.
            int key = VrFingerRig.Key(0.5f, 10, -1);
            bool steady = true;
            for (int i = 0; i < 40; i++)
            {
                int next = VrFingerRig.Key(0.5f + (i % 2 == 0 ? 0.02f : -0.02f), 10, key);
                steady &= next == key;
                key = next;
            }

            Check("a curl jittering +-0.02 on a key boundary keeps its key", steady);
            Check("a full key's move switches the key", VrFingerRig.Key(0.5f + 1f / 9f, 10, key) != key);
            Check("the ends are reached", VrFingerRig.Key(1f, 10, 8) == 9 && VrFingerRig.Key(0f, 10, 1) == 0);

            // 482: the touch rules, on the physical controls.
            const uint bound = VrFingerRig.TouchTriggerBound | VrFingerRig.TouchThumbBound;
            Vec3 Free(float trigger, float squeeze, uint touch) => VrFingerRig.FreeCurls(trigger, squeeze, touch, 0.25f, 0.6f);
            Check("open, nothing touched: all relaxed", Free(0f, 0f, bound).Length < 1e-6f);
            Check("a finger resting on the trigger moves the index a little",
                  Math.Abs(Free(0f, 0f, bound | VrFingerRig.TouchTrigger).y - 0.25f) < 1e-6f);
            Check("trigger touch while gripping closes the index",
                  Math.Abs(Free(0f, 0.8f, bound | VrFingerRig.TouchTrigger).y - 1f) < 1e-6f);
            Check("the grip closes middle, ring and pinky", Math.Abs(Free(0f, 0.7f, bound).z - 0.7f) < 1e-6f);
            Check("the thumb on the stick rests at 0.6", Math.Abs(Free(0f, 0f, bound | VrFingerRig.TouchThumb).x - 0.6f) < 1e-6f);
            Check("a bound thumb sensor not touched keeps the thumb up, even gripping", Free(0f, 1f, bound).x == 0f);
            Check("a pull past the touch closes the index by the pull",
                  Math.Abs(Free(0.6f, 0f, bound | VrFingerRig.TouchTrigger).y - 0.6f) < 1e-6f);
            // 494: the touched index closes with the squeeze - it jumped shut at half.
            float worst = 0f;
            for (float sq = 0f; sq < 0.995f; sq += 0.01f)
                worst = Math.Max(worst, Math.Abs(Free(0f, sq + 0.01f, bound | VrFingerRig.TouchTrigger).y
                                                 - Free(0f, sq, bound | VrFingerRig.TouchTrigger).y));
            Check("the touched index closes with the grip, no jump (largest step " + worst.ToString("F3") + " per 0.01)", worst <= 0.05f);
            Check("unbound sensors: the pull stands in for the trigger touch, the grip for the thumb",
                  Math.Abs(Free(0.1f, 0f, 0u).y - 0.25f) < 1e-6f && Free(0f, 0f, 0u).y == 0f
                  && Math.Abs(Free(0f, 0.6f, 0u).x - 0.6f) < 1e-6f && Free(0f, 0.4f, 0u).x == 0f);

            VrFingerRig.Engaged(0f, 0u, out bool ei, out bool et);
            Check("holding with no sensors: index and thumb stay on the handle", ei && et);
            VrFingerRig.Engaged(0f, bound, out ei, out et);
            Check("holding, nothing touched: index and thumb come off", !ei && !et);
            VrFingerRig.Engaged(0.3f, bound | VrFingerRig.TouchThumb, out ei, out et);
            Check("holding, pulling and thumb touched: both on", ei && et);
        }

        /// <summary>TEST 504: a hand whose four fingers are one block, modelled as a
        /// fist round the grip (battania_warlord_bracers).</summary>
        private static void Mitten()
        {
            Section("TEST 504 mitten hands: four fingers in one curled block");
            const float rad = (float)(Math.PI / 180.0);
            var pts = new System.Collections.Generic.List<Vec3>();
            // The palm and the back of the hand, behind the knuckles.
            for (float x = 0f; x <= 0.12f; x += 0.006f)
                for (float z = -0.04f; z <= 0.0401f; z += 0.008f)
                {
                    pts.Add(new Vec3(x, -0.03f, z, -1f));
                    if (x < 0.07f)
                        pts.Add(new Vec3(x, 0.0f, z, -1f));
                }

            // The block: a middle finger's bones from (0.12, -0.018), modelled 100/80/20 deg,
            // a 1.1 cm tube swept across the knuckles.
            float len = 0.102f;
            float[] bone = { 0.49f * len, 0.30f * len, 0.21f * len };
            float[] turn = { 100f * rad, 180f * rad, 200f * rad };
            float u = 0.12f, w = -0.018f;
            for (int b = 0; b < 3; b++)
            {
                for (float t = 0f; t <= bone[b]; t += 0.004f)
                {
                    float cu = u + t * (float)Math.Cos(turn[b]), cw = w + t * (float)Math.Sin(turn[b]);
                    for (int a = 0; a < 8; a++)
                    {
                        double ang = a * Math.PI / 4;
                        for (float z = -0.04f; z <= 0.0401f; z += 0.01f)
                            pts.Add(new Vec3(cu + 0.011f * (float)Math.Cos(ang), cw + 0.011f * (float)Math.Sin(ang), z, -1f));
                    }
                }

                u += bone[b] * (float)Math.Cos(turn[b]);
                w += bone[b] * (float)Math.Sin(turn[b]);
            }

            var grip = new Vec3(0.09f, 0.008f, 0f, -1f);
            VrFingerRig.Fit fit = VrFingerRig.FitHand(pts, 0.09f, 0.012f, null, grip);
            Check("a curled block with no finger columns fits as a mitten (" + fit.Why + ")", fit.Valid && fit.Mitten);
            if (!fit.Valid || !fit.Mitten)
                return;

            Vec3 rest = fit.Rest[2] * (1f / rad);
            Check("its modelled angles are the block's, 100/80/20 (got " + rest.x.ToString("F0") + "/" + rest.y.ToString("F0") + "/"
                  + rest.z.ToString("F0") + ")", Math.Abs(rest.x - 100f) < 15f && Math.Abs(rest.y - 80f) < 15f && Math.Abs(rest.z - 20f) < 25f);

            int block = 0, palm = 0, stray = 0;
            Vec3 tip = Vec3.Zero, tipW = Vec3.Zero;
            float far = float.MinValue;
            foreach (Vec3 v in pts)
            {
                int d = VrFingerRig.Weigh(fit, v, out Vec3 wv);
                bool isPalm = v.x < 0.095f && v.y < 0.005f;
                if (isPalm)
                {
                    palm += d < 0 ? 1 : 0;
                    stray += d >= 0 && wv.x + wv.y + wv.z > 0.5f ? 1 : 0;
                }
                else if (d == 2)
                {
                    block++;
                }

                // The block's far end (its tip, curled back over the palm).
                if (d == 2 && wv.z > 0.99f && v.y > far)
                {
                    far = v.y;
                    tip = v;
                    tipW = wv;
                }
            }

            Check("the block rides the middle finger, the palm stays (block " + block + ", palm kept " + palm + ", palm moved " + stray + ")",
                  block > 200 && stray == 0);
            Expect("bent to its modelled pose, the block stays where it is", VrFingerRig.Bend(fit, 2, fit.Rest[2], tip, tipW), tip);
            Vec3 open = VrFingerRig.Bend(fit, 2, new Vec3(12f, 22f, 10f, -1f) * rad, tip, tipW);
            Check("relaxed, the block opens - its tip swings out past the knuckles (x " + tip.x.ToString("F3") + " -> "
                  + open.x.ToString("F3") + ")", open.x > 0.17f);
            // 509: the block is glued where it lies on the palm - a block point beside a
            // palm point barely moves, one far from any keeps its weight.
            var gd = new int[pts.Count];
            var gw = new Vec3[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                gd[i] = VrFingerRig.Weigh(fit, pts[i], out gw[i]);
            var before = (Vec3[])gw.Clone();
            VrFingerRig.Glue(fit, pts, gd, gw);
            float nearest = float.MaxValue, nearW = 1f, farFrom = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                if (gd[i] < 0 || before[i].x + before[i].y + before[i].z < 0.99f)
                    continue;
                float d = float.MaxValue;
                for (int j = 0; j < pts.Count; j++)
                {
                    if (gd[j] < 0)
                        d = Math.Min(d, (pts[j] - pts[i]).Length);
                }
                float wsum = gw[i].x + gw[i].y + gw[i].z;
                if (d < nearest)
                {
                    nearest = d;
                    nearW = wsum;
                }
                if (d > 0.025f)
                    farFrom = Math.Max(farFrom, wsum);
            }
            Check("509: glued - the block point nearest the palm (" + (nearest * 1000f).ToString("F0") + " mm) moves less (weight "
                  + nearW.ToString("F2") + "), one far from it in full (" + farFrom.ToString("F2") + ")",
                  nearest < 0.02f && nearW < 0.95f && farFrom > 0.99f);

            Check("a mitten's fingers do not slide apart", VrFingerRig.SideBySide(fit, new Vec3[5], new Vec3[5], new float[5][], new Vec3(0f, 0f, 1f, -1f), 0f, 0.01f)[1].Length == 0f);
        }

        /// <summary>TEST 590: the split's ABI, its patches' parameter names, and
        /// the hand-bin packer (no overlaps, inside the bins, overflow refused).</summary>
        private static void UiSplit()
        {
            Section("interface image split (TEST 590)");

            Report(Marshal.SizeOf(typeof(BvrUiSplit)) == 36 &&
                   Marshal.OffsetOf(typeof(BvrUiSplit), "HudWidthM").ToInt32() == 32,
                   "BvrUiSplit is 36 bytes with hudWidthM last, as bvr_api.h has it");

            const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            Assembly asm = typeof(VrMelee).Assembly;

            // The remap postfixes bind screenX / screenY by name on all three
            // (static) WorldToScreen variants.
            foreach (string[] pair in new[]
                     {
                         new[] { "WorldToScreenPatch", "WorldToScreen" },
                         new[] { "WorldToScreenInsideUsableAreaPatch", "WorldToScreenInsideUsableArea" },
                         new[] { "WorldToScreenWithFixedZPatch", "WorldToScreenWithFixedZ" },
                     })
            {
                MethodInfo target = typeof(MBWindowManager).GetMethod(pair[1], Any);
                MethodInfo post = asm.GetType("BannerlordVR.Patches." + pair[0])?.GetMethod("Postfix", Any);
                var names = new System.Collections.Generic.HashSet<string>();
                if (target != null)
                    foreach (ParameterInfo p in target.GetParameters())
                        names.Add(p.Name);
                bool ok = target != null && post != null;
                if (ok)
                    foreach (ParameterInfo p in post.GetParameters())
                        ok &= names.Contains(p.Name);
                Report(ok, pair[0] + ".Postfix binds screenX/screenY on MBWindowManager." + pair[1]);
            }
            Type page = asm.GetType("BannerlordVR.Patches.UiPageSizePatch");
            var targets = page?.GetMethod("TargetMethods", Any)?.Invoke(null, null)
                as System.Collections.Generic.IEnumerable<MethodBase>;
            int count = 0;
            if (targets != null)
                foreach (MethodBase _ in targets)
                    count++;
            Report(count == 2, "the page-size patch finds the platform's Width and Height getters (" + count + ")");
            int Targets(string patch)
            {
                var found = asm.GetType("BannerlordVR.Patches." + patch)?.GetMethod("TargetMethods", Any)
                    ?.Invoke(null, null) as System.Collections.Generic.IEnumerable<MethodBase>;
                int n = 0;
                if (found != null)
                    foreach (MethodBase _ in found)
                        n++;
                return n;
            }
            int layouts = Targets("UiHandShiftPatch");
            Report(layouts >= 5, "the hand-move hook finds every ILayout.OnLayout (" + layouts + "; TEST 591)");
            int draws = Targets("UiSupersamplePatch");
            Report(draws == 2, "the clip-scale patch finds DrawImage and DrawText (" + draws + "; TEST 591)");
            Report(Type.GetType("TaleWorlds.Engine.GauntletUI.TwoDimensionEnginePlatform, TaleWorlds.Engine.GauntletUI")
                       ?.GetField("_activeScissor", BindingFlags.Instance | BindingFlags.NonPublic) != null,
                   "TwoDimensionEnginePlatform._activeScissor is still there for the clip scale");
            Report(asm.GetType("BannerlordVR.Patches.UiPageOriginPatch") != null &&
                   Type.GetType("TaleWorlds.GauntletUI.EventManager, TaleWorlds.GauntletUI")?.GetMethod("UpdatePositions",
                       BindingFlags.Instance | BindingFlags.NonPublic) != null,
                   "EventManager.UpdatePositions is still there for the page origin");

            Type wrist = asm.GetType("BannerlordVR.VR.VrWristHud");
            MethodInfo begin = wrist?.GetMethod("BeginPlan", Any);
            MethodInfo place = wrist?.GetMethod("Place", Any);
            if (begin == null || place == null)
            {
                Fail("VrWristHud.BeginPlan / Place not found (renamed?)");
                return;
            }

            // A 2560x1440 window: the bin under the HUD, the strip under the world.
            float[] a = { 1472, 676, 1088, 764 }, b = { 0, 1216, 1472, 224 };
            var bins = (float[][])asm.GetType("BannerlordVR.VR.VrUiSplit").GetField("HandBins", Any).GetValue(null);
            Array.Copy(a, bins[0], 4);
            Array.Copy(b, bins[1], 4);
            begin.Invoke(null, null);

            // The campaign palette at 1440p as the 590 run logged it - centre panel,
            // bar's right side, nav bar, chat - packed tallest first (TEST 591; in
            // slot order the chat fit nowhere and was punched into the world panel).
            float[][] items = { new float[] { 573, 95 }, new float[] { 818, 160 },
                                new float[] { 795, 93 }, new float[] { 660, 403 } };
            Array.Sort(items, (p, q) => q[1].CompareTo(p[1]));
            var placed = new System.Collections.Generic.List<float[]>();
            bool allIn = true;
            foreach (float[] it in items)
            {
                object[] args = { it[0], it[1], 0f, 0f };
                if (!(bool)place.Invoke(null, args))
                {
                    allIn = false;
                    continue;
                }
                float x = (float)args[2], y = (float)args[3];
                bool inA = x >= a[0] && y >= a[1] && x + it[0] <= a[0] + a[2] && y + it[1] <= a[1] + a[3];
                bool inB = x >= b[0] && y >= b[1] && x + it[0] <= b[0] + b[2] && y + it[1] <= b[1] + b[3];
                allIn &= inA || inB;
                placed.Add(new[] { x, y, it[0], it[1] });
            }
            Report(allIn && placed.Count == items.Length, "every palette piece lands inside a bin");

            bool overlap = false;
            for (int i = 0; i < placed.Count; i++)
                for (int j = i + 1; j < placed.Count; j++)
                    overlap |= placed[i][0] < placed[j][0] + placed[j][2] && placed[j][0] < placed[i][0] + placed[i][2] &&
                               placed[i][1] < placed[j][1] + placed[j][3] && placed[j][1] < placed[i][1] + placed[i][3];
            Report(!overlap, "no two pieces overlap (each crop holds only its own widget)");

            object[] huge = { 600f, 1400f, 0f, 0f };
            Report(!(bool)place.Invoke(null, huge), "a piece taller than every bin is refused (stays punched in place)");
        }

        private static void DeploymentCommanderHit()
        {
            Section("pre-battle commander hit testing");
            const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
            Type widgetType = typeof(TaleWorlds.GauntletUI.BaseTypes.Widget);
            Type vectorType = typeof(TaleWorlds.TwoDimension.Rectangle2D).GetField("TopLeft").FieldType;
            object Vector(float x, float y) => Activator.CreateInstance(vectorType, x, y);
            void Bounds(TaleWorlds.GauntletUI.BaseTypes.Widget w, float x, float y, float width, float height)
            {
                object rect = TaleWorlds.TwoDimension.Rectangle2D.Create();
                rect.GetType().GetField("LocalPosition").SetValue(rect, Vector(x, y));
                rect.GetType().GetField("LocalScale").SetValue(rect, Vector(width, height));
                var bounds = (TaleWorlds.TwoDimension.Rectangle2D)rect;
                bounds.CalculateMatrixFrame(TaleWorlds.TwoDimension.Rectangle2D.Invalid);
                w.AreaRect = bounds;
            }
            TaleWorlds.GauntletUI.BaseTypes.Widget Make(string id, TaleWorlds.GauntletUI.BaseTypes.Widget parent)
            {
                // Only the hit-test tree is needed; no renderer or native input context.
                var w = (TaleWorlds.GauntletUI.BaseTypes.Widget)System.Runtime.Serialization.FormatterServices
                    .GetUninitializedObject(widgetType);
                widgetType.GetField("_children", Fields).SetValue(w,
                    new System.Collections.Generic.List<TaleWorlds.GauntletUI.BaseTypes.Widget>());
                widgetType.GetField("_parent", Fields).SetValue(w, parent);
                widgetType.GetField("_id", Fields).SetValue(w, id);
                parent?.Children.Add(w);
                return w;
            }
            var root = Make("Root", null);
            var container = Make("", root);
            var pool = Make("CaptainPool", container);
            var portrait = Make("Portrait", pool);
            Bounds(root, 0f, 0f, 2560f, 1440f);
            Bounds(container, 1230f, 12f, 100f, 60f);
            Bounds(pool, 1734f, 676f, 100f, 60f);
            Bounds(portrait, 1760f, 688f, 30f, 30f);
            MethodInfo collect = typeof(TaleWorlds.GauntletUI.EventManager)
                .GetMethod("CollectEnableWidgetsAt", BindingFlags.Static | BindingFlags.NonPublic);
            bool HitsPortrait()
            {
                var hits = new System.Collections.Generic.List<TaleWorlds.GauntletUI.BaseTypes.Widget>();
                collect.Invoke(null, new[] { (object)root, Vector(1775f, 703f), hits });
                return hits.Contains(portrait);
            }
            Report(!HitsPortrait(), "a visible commander outside its parent is skipped by vanilla mouse events");
            Type wrist = typeof(VrMelee).Assembly.GetType("BannerlordVR.VR.VrWristHud");
            var slots = (Array)wrist.GetField("Slots", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            foreach (object slot in slots)
            {
                Type t = slot.GetType();
                if ((string)t.GetField("Layer").GetValue(slot) != "MissionOrderOfBattle")
                    continue;
                var find = (Func<TaleWorlds.GauntletUI.BaseTypes.Widget, TaleWorlds.GauntletUI.BaseTypes.Widget>)
                    t.GetField("Find").GetValue(slot);
                Bounds(find(root), 1734f, 676f, 100f, 60f);
                break;
            }
            Report(HitsPortrait(), "moving the selected commander container lets vanilla mouse events reach its portraits");
        }

        private static void SiegeAndLadder()
        {
            Section("siege machines and ladders by hand (TESTs 510-512)");

            const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            Assembly asm = typeof(VrSkinnedWeapon).Assembly;
            Type siege = asm.GetType("BannerlordVR.VR.VrSiege");
            Type ladder = asm.GetType("BannerlordVR.VR.VrLadder");
            MethodInfo wrap = siege?.GetMethod("WrapPi", Any);
            MethodInfo target = siege?.GetMethod("Target", Any);
            MethodInfo pressed = siege?.GetMethod("Pressed", Any);
            MethodInfo stick = ladder?.GetMethod("StickFor", Any);
            if (wrap == null || target == null || pressed == null || stick == null)
            {
                Fail("VrSiege.WrapPi/Target/Pressed or VrLadder.StickFor not found via reflection");
                return;
            }

            float W(float a) => (float)wrap.Invoke(null, new object[] { a });
            float Pi = (float)Math.PI;
            Approx("wrap: 350 deg is -10", W(350f * Pi / 180f), -10f * Pi / 180f);
            Approx("wrap: -190 deg is 170", W(-190f * Pi / 180f), 170f * Pi / 180f);
            Approx("wrap: pi stays pi", W(Pi), Pi);
            Approx("wrap: -pi is pi", W(-Pi), Pi);

            Vec2 T(float dir0, float rel0, float dYaw, float dElev, float ys, float ps, float pg) =>
                (Vec2)target.Invoke(null, new object[] { dir0, rel0, dYaw, dElev, ys, ps, pg });

            // The hand swept across the -x axis: atan2 jumps from +179 to -179 deg,
            // the machine must turn 2 deg, not 358.
            float a0 = 179f * Pi / 180f, a1 = -179f * Pi / 180f;
            Vec2 across = T(0.3f, 0.1f, a1 - a0, 0f, 1f, 1f, 1f);
            Approx("aim: a turn across +-pi is the short way (yaw)", across.x, 0.3f + 2f * Pi / 180f);
            Approx("aim: no elevation change keeps the release angle", across.y, 0.1f);
            Vec2 still = T(0.3f, 0.1f, 0f, 0f, 1f, 1f, 1f);
            Check("aim: hands where they took hold is where the machine stood", Near(still.x, 0.3f) && Near(still.y, 0.1f));
            Vec2 flipped = T(0f, 0f, 0.2f, 0.1f, -1f, -1f, 2f);
            Check("aim: the signs flip, the gain scales elevation only",
                  Near(flipped.x, -0.2f) && Near(flipped.y, -0.2f));

            MethodInfo pilotFrame = siege.GetMethod("PilotFrame", Any);
            MethodInfo aimOffset = siege.GetMethod("AimOffset", Any);
            if (pilotFrame == null || aimOffset == null)
                Fail("VrSiege.PilotFrame/AimOffset not found via reflection");
            else
            {
                MatrixFrame pin = MatrixFrame.Identity;
                pin.origin = new Vec3(0f, -1f, 1f);
                MatrixFrame PinPose(MatrixFrame machine, float turn, Vec3 slide) =>
                    (MatrixFrame)pilotFrame.Invoke(null, new object[] { machine, pin, turn, slide });
                Vec3 D(MatrixFrame machine, Vec3 hand, Vec3 pivot) =>
                    (Vec3)aimOffset.Invoke(null, new object[] { machine, hand, pivot });

                MatrixFrame land = PinPose(MatrixFrame.Identity, 0f, Vec3.Zero);
                Expect("pilot: stationary land machine keeps its position", land.origin, pin.origin);
                Expect("pilot: stationary land machine keeps its facing", land.rotation.f, pin.rotation.f);

                MatrixFrame ship = MatrixFrame.Identity;
                ship.origin = new Vec3(30f, 40f, 2f);
                ship.rotation.RotateAboutUp(Pi / 2f);
                MatrixFrame carried = PinPose(ship, 0f, Vec3.Zero);
                Expect("pilot: ship translation and yaw carry the pilot", carried.origin, new Vec3(31f, 40f, 3f));
                Expect("pilot: ship yaw carries the pilot's facing", carried.rotation.f, new Vec3(-1f, 0f, 0f));

                ship.rotation.RotateAboutSide(0.2f);
                ship.rotation.RotateAboutForward(-0.1f);
                carried = PinPose(ship, 0f, Vec3.Zero);
                Expect("pilot: ship pitch and roll keep the pilot on deck",
                       ship.TransformToLocalNonOrthogonal(carried).origin, pin.origin);
                Expect("pilot: ship pitch and roll carry the pilot's up", carried.rotation.u, ship.rotation.u);
                MatrixFrame moved = ship.TransformToLocalNonOrthogonal(PinPose(ship, 0.3f, new Vec3(0.2f, 0f, 0f)));
                Expect("pilot: stick slide stays relative to the moving deck", moved.origin, new Vec3(0.2f, -1f, 1f));
                Expect("pilot: stick turn stays relative to the moving deck", moved.rotation.f,
                       new Vec3(-(float)Math.Sin(0.3f), (float)Math.Cos(0.3f), 0f));

                Vec3 pivot = new Vec3(0f, 0f, 1f), hand = new Vec3(0f, -1f, 1.2f);
                Vec3 before = D(MatrixFrame.Identity, hand, pivot);
                Vec3 after = D(ship, ship.TransformToParent(hand), ship.TransformToParent(pivot));
                Expect("aim: ship movement and rocking do not move the hand target", after, before);
                hand.x += 0.2f;
                Expect("aim: a deliberate hand move still aims on a moving ship",
                       D(ship, ship.TransformToParent(hand), ship.TransformToParent(pivot)), new Vec3(0.2f, -1f, 0.2f));
            }

            float S(float below, float full, float sign) => (float)stick.Invoke(null, new object[] { below, 0f, full, sign });
            Approx("ladder: the hand on its latch is no climb", S(0f, 0.1f, 1f), 0f);
            Approx("ladder: 5 cm below of 10 is half forward", S(0.05f, 0.1f, 1f), 0.5f);
            Approx("ladder: far below is full forward, clamped", S(0.5f, 0.1f, 1f), 1f);
            Approx("ladder: above the latch climbs down, clamped", S(-0.5f, 0.1f, 1f), -1f);
            Approx("ladder: ladder_sign -1 flips it", S(0.05f, 0.1f, -1f), -0.5f);

            // TEST 511b: the deadband the climb settles in.
            float SD(float below) => (float)stick.Invoke(null, new object[] { below, 0.02f, 0.1f, 1f });
            Approx("ladder: inside the 2 cm deadband, no climb", SD(0.015f), 0f);
            Approx("ladder: 7 cm below with a 2 cm deadband is half", SD(0.07f), 0.5f);
            Approx("ladder: the deadband works climbing down too", SD(-0.07f), -0.5f);

            MethodInfo seg = ladder.GetMethod("SegmentDistance", Any);
            if (seg == null)
                Fail("VrLadder.SegmentDistance not found via reflection");
            else
            {
                float D(Vec3 p) => (float)seg.Invoke(null, new object[] { p, new Vec3(0f, 0f, 0f, -1f), new Vec3(0f, 2f, 4f, -1f) });
                Approx("ladder line: a point on it is 0 away", D(new Vec3(0f, 1f, 2f, -1f)), 0f);
                Approx("ladder line: 0.4 m to the side of it", D(new Vec3(0.4f, 1f, 2f, -1f)), 0.4f);
                Approx("ladder line: past the top end, from the end", D(new Vec3(0f, 2f, 5f, -1f)), 1f);
            }

            MethodInfo rotZ = siege.GetMethod("RotateZ", Any);
            if (rotZ == null)
                Fail("VrSiege.RotateZ not found via reflection");
            else
                Expect("grab offset turned 90 deg with the machine",
                       (Vec3)rotZ.Invoke(null, new object[] { new Vec3(0.3f, 0f, 0.1f, -1f), Pi / 2f }),
                       new Vec3(0f, 0.3f, 0.1f, -1f));

            // TEST 512d: a hand 1 m behind the pivot (barrel along +x), lowered 0.2 m,
            // lifts the barrel by atan(0.2) - the lever is geometry, no sign flip.
            MethodInfo liftM = siege.GetMethod("Lift", Any);
            if (liftM == null)
                Fail("VrSiege.Lift not found via reflection");
            else
            {
                Vec3 barrel = new Vec3(1f, 0f, 0f, -1f);
                float L(Vec3 d) => (float)liftM.Invoke(null, new object[] { d, (float)Math.Sqrt(d.x * d.x + d.y * d.y), barrel });
                float lifted = W(L(new Vec3(-1f, 0f, -0.2f, -1f)) - L(new Vec3(-1f, 0f, 0f, -1f)));
                Approx("lever: hands down behind the pivot lift the barrel", lifted, (float)Math.Atan(0.2));
                Vec2 lev = T(0f, 0f, 0f, L(new Vec3(-1f, 0f, -0.2f, -1f)) - L(new Vec3(-1f, 0f, 0f, -1f)), 1f, 1f, 1f);
                Approx("lever: the wrap across pi keeps it small", lev.y, (float)Math.Atan(0.2));
            }

            // TEST 519: the ring's sampled turn, its inverse, and the ratchet.
            Type ballista = asm.GetType("BannerlordVR.VR.VrBallista");
            MethodInfo ringAt = ballista?.GetMethod("RingAt", Any);
            MethodInfo paramFor = ballista?.GetMethod("ParamFor", Any);
            MethodInfo ratchet = ballista?.GetMethod("Ratchet", Any);
            if (ringAt == null || paramFor == null || ratchet == null)
                Fail("VrBallista.RingAt/ParamFor/Ratchet not found via reflection");
            else
            {
                float[] ring = { 0f, 1f, 1.2f, 4f };   // a still stretch in the middle
                float R(float p) => (float)ringAt.Invoke(null, new object[] { ring, p });
                float Pf(float r) => (float)paramFor.Invoke(null, new object[] { ring, r });
                Approx("ring: none at the start", R(0f), 0f);
                Approx("ring: between samples it interpolates", R(0.5f), 1.1f);
                Approx("ring: all of it at the end, clamped past", R(1.5f), 4f);
                Approx("ring: the parameter for a turn inverts it", Pf(R(0.8f)), 0.8f);
                Approx("ring: before the start is 0", Pf(-1f), 0f);
                Approx("ring: past the end is 1", Pf(9f), 1f);

                float Ra(float hand, float r, ref float off)
                {
                    object[] a = { hand, r, off };
                    float got = (float)ratchet.Invoke(null, a);
                    off = (float)a[2];
                    return got;
                }
                float o = 0.5f;
                Approx("ratchet: forward drives the ring", Ra(1.5f, 0.5f, ref o), 1.0f);
                o = 0.5f;
                Approx("ratchet: backward asks no less than the ring", Ra(0.2f, 0.5f, ref o), 0.5f);
                Approx("ratchet: and the hold slips back with the hand", o, -0.3f);
                Approx("ratchet: forward again drives at once from the slip", Ra(0.4f, 0.5f, ref o), 0.7f);
            }

            // TEST 522: a bolt laid along the groove seats; one carried across it does not.
            MethodInfo inGroove = ballista?.GetMethod("InGroove", Any);
            if (inGroove == null)
                Fail("VrBallista.InGroove not found via reflection");
            else
            {
                Vec3 tail = new Vec3(0f, 0f, 1f, -1f), dir = new Vec3(0f, 1f, 0f, -1f);
                float cos35 = (float)Math.Cos(35.0 * Math.PI / 180.0);
                bool G(Vec3 at, Vec3 f) => (bool)inGroove.Invoke(null, new object[] { at, f, tail, dir, 1.5f, 0.2f, cos35, 0f, 0f, 0f });
                Check("groove: along it, over its middle, seats", G(new Vec3(0.05f, 0.7f, 1.1f, -1f), dir));
                Check("groove: along it near the tail seats too", G(new Vec3(0f, 0.1f, 1.05f, -1f), dir));
                Check("groove: pointing back along it seats (either way)", G(new Vec3(0f, 0.7f, 1f, -1f), new Vec3(0f, -1f, 0f, -1f)));
                Check("groove: across it does not", !G(new Vec3(0f, 0.7f, 1f, -1f), new Vec3(1f, 0f, 0f, -1f)));
                Check("groove: 0.3 m above it does not", !G(new Vec3(0f, 0.7f, 1.3f, -1f), dir));
                Check("groove: past its tip does not", !G(new Vec3(0f, 1.9f, 1f, -1f), dir));

                float seatReach = (float)ballista.GetField("SeatM", Any).GetValue(null);
                float seatCos = (float)ballista.GetField("SeatCos", Any).GetValue(null);
                bool EasySeat(Vec3 at, Vec3 f) => (bool)inGroove.Invoke(null,
                    new object[] { at, f, tail, dir, 1.5f, seatReach, seatCos, 0f, 0f, 0f });
                Check("loading: a bolt held sideways over the groove now seats",
                      EasySeat(new Vec3(0f, 0.7f, 1.03f), new Vec3(1f, 0f, 0f)));
                Check("loading: 40 cm above the groove seats without precise alignment",
                      EasySeat(new Vec3(0f, 0.7f, 1.4f), new Vec3(0f, 0f, 1f)));
                Check("loading: a bolt down at the ammo pile does not seat",
                      !EasySeat(new Vec3(0f, 0.7f, 0.2f), dir));
                Check("loading: a bolt far beyond the tip does not seat",
                      !EasySeat(new Vec3(0f, 2.1f, 1f), dir));

                MethodInfo rackDistance = asm.GetType("BannerlordVR.VR.VrGrab").GetMethod("BoxDistance", Any);
                float rackReach = (float)ballista.GetField("RackM", Any).GetValue(null);
                float RackDistance(float z) => (float)rackDistance.Invoke(null, new object[] {
                    new Vec3(0f, 0f, z), new Vec3(-0.3f, -0.3f, 0f), new Vec3(0.3f, 0.3f, 0.1f) });
                Check("ammo pickup: a hand 60 cm above the pile is within reach", RackDistance(0.7f) <= rackReach);
                Check("ammo pickup: a hand 90 cm above the pile stays out of reach", RackDistance(1f) > rackReach);
            }

            // TEST 521: the stair view - a line fitted to height over distance walked.
            MethodInfo fitAt = asm.GetType("BannerlordVR.VR.VrCameraDriver")?.GetMethod("FitAt", Any);
            if (fitAt == null)
                Fail("VrCameraDriver.FitAt not found via reflection");
            else
            {
                const int n = 81;
                float[] path = new float[n], slope = new float[n], stairs = new float[n];
                for (int k = 0; k < n; k++)
                {
                    path[k] = k * 0.01f;
                    slope[k] = 0.5f * path[k];
                    stairs[k] = 0.2f * (float)Math.Floor(path[k] / 0.3f);   // 0.2 m risers, 0.3 m treads
                }
                float Fit(float[] z) => (float)fitAt.Invoke(null, new object[] { path, z, n, 0.8f });
                Approx("stairs view: a slope is exact (no lag)", Fit(slope), 0.4f);
                float off = Fit(stairs) - stairs[n - 1];
                Check("stairs view: a staircase is within half a riser of the step (" + off.ToString("F3") + " m)",
                      Math.Abs(off) <= 0.1f);
            }

            // TEST 520: a rung is a segment; the grip snaps onto it.
            MethodInfo closest = asm.GetType("BannerlordVR.VR.VrLadder")?.GetMethod("Closest", Any);
            if (closest == null)
                Fail("VrLadder.Closest not found via reflection");
            else
            {
                Vec3 C(Vec3 p) => (Vec3)closest.Invoke(null, new object[] { p, new Vec3(-0.3f, 0f, 1f, -1f), new Vec3(0.3f, 0f, 1f, -1f) });
                Expect("rung: a hand above it snaps down onto it", C(new Vec3(0.1f, 0.05f, 1.08f, -1f)), new Vec3(0.1f, 0f, 1f, -1f));
                Expect("rung: past its end, its end", C(new Vec3(0.6f, 0f, 1f, -1f)), new Vec3(0.3f, 0f, 1f, -1f));
            }

            bool P(float v, bool was) => (bool)pressed.Invoke(null, new object[] { v, was });
            Check("press: 0.5 does not press from up", !P(0.5f, false));
            Check("press: 0.5 holds once down (hysteresis)", P(0.5f, true));
            Check("press: 0.7 presses", P(0.7f, false));
            Check("press: 0.39 lets go", !P(0.39f, true));
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

        /// <summary>
        /// The arm IK's geometry, which is the half of it that can be checked
        /// without a running mission.
        ///
        /// The properties asserted here are the ones whose failure is invisible
        /// rather than loud: an elbow that is off its circle changes the segment
        /// lengths, which reads as the forearm growing and shrinking, and an arm
        /// that cannot straighten reads as "the IK feels stiff" long before anyone
        /// thinks to measure it.
        /// </summary>
        private static void ArmIkGeometry()
        {
            Console.WriteLine();
            Console.WriteLine("Arm IK geometry");

            const float a = 0.30f;      // upper arm
            const float b = 0.28f;      // forearm
            Vec3 shoulder = new Vec3(0f, 0f, 0f);
            Vec3 pole = new Vec3(0f, -0.3f, -1f);

            Vec3 elbow;
            float ua, ub;

            // 1. A reachable target. The two segments must actually connect the
            //    shoulder to the target - this is the definition of a correct
            //    solve, and it is worth asserting rather than assuming because
            //    every later stage changes the pole and none of them may break it.
            Vec3 mid = new Vec3(0.25f, 0.30f, -0.10f);
            Check("reachable target solves",
                VrArmIk.SolveElbowWithBend(shoulder, mid, pole, a, b, out elbow, out ua, out ub));
            Approx("upper segment length", (elbow - shoulder).Length, ua);
            Approx("fore segment length", (mid - elbow).Length, ub);
            Approx("lengths unchanged when reachable", ua + ub, a + b);

            // 2. Beyond reach: STRETCH, not clamp. The wrist must still land
            //    exactly on the target and the arm must be straight. Clamping
            //    instead is the specific bug that leaves an arm permanently bent,
            //    so this asserts the thing that distinguishes them.
            Vec3 far = new Vec3(0f, 1.2f, 0f);
            Check("out-of-reach target solves",
                VrArmIk.SolveElbowWithBend(shoulder, far, pole, a, b, out elbow, out ua, out ub));
            Approx("stretched upper reaches", (elbow - shoulder).Length, ua);
            Approx("stretched fore reaches", (far - elbow).Length, ub);
            Approx("segments now span the distance", ua + ub, (far - shoulder).Length);

            float straight = Vec3.DotProduct((elbow - shoulder).NormalizedCopy(),
                                             (far - elbow).NormalizedCopy());
            Check("arm is straight past full reach", straight > 0.9999f);

            // 3. Arm hanging straight down, at almost exactly full extension. This
            //    is the pose the whole stretch-not-clamp choice exists for, and the
            //    one where the pole is nearly parallel to the arm axis - so it also
            //    covers the degenerate-pole path.
            Vec3 down = new Vec3(0f, 0f, -(a + b) * 0.999f);
            Check("arm straight down solves",
                VrArmIk.SolveElbowWithBend(shoulder, down, pole, a, b, out elbow, out ua, out ub));
            Approx("upper still attached", (elbow - shoulder).Length, ua);
            Approx("fore still attached", (down - elbow).Length, ub);

            float downStraight = Vec3.DotProduct((elbow - shoulder).NormalizedCopy(),
                                                 (down - elbow).NormalizedCopy());
            Check("hanging arm is nearly straight", downStraight > 0.99f);

            // 4. Target folded closer than the segments can reach. The triangle has
            //    no solution; the solve must stay finite rather than produce NaN,
            //    because a NaN here is written into the skeleton and stays there.
            Vec3 tucked = new Vec3(0.01f, 0.01f, 0f);
            Check("degenerate fold solves",
                VrArmIk.SolveElbowWithBend(shoulder, tucked, pole, a, b, out elbow, out ua, out ub));
            Check("degenerate fold is finite",
                !float.IsNaN(elbow.x) && !float.IsNaN(elbow.y) && !float.IsNaN(elbow.z)
                && !float.IsInfinity(elbow.x) && !float.IsInfinity(elbow.y)
                && !float.IsInfinity(elbow.z));

            // 5. The elbow must lie ON its circle: its displacement from the
            //    shoulder-to-wrist line has to be perpendicular to that line. This
            //    is the invariant every later stage must preserve while it moves
            //    the elbow around the circle, so it is the one to guard now.
            Check("mid-reach target solves again",
                VrArmIk.SolveElbowWithBend(shoulder, mid, pole, a, b, out elbow, out ua, out ub));
            Vec3 axis = (mid - shoulder).NormalizedCopy();
            Vec3 fromLine = elbow - shoulder;
            Vec3 perp = fromLine - axis * Vec3.DotProduct(fromLine, axis);
            Check("elbow displacement is perpendicular to the arm axis",
                Math.Abs(Vec3.DotProduct(perp.NormalizedCopy(), axis)) < 1e-3f);

            // 6. The elbow goes to the side the pole names. With a down-and-back
            //    pole and the hand held out forward, the elbow must end up BELOW
            //    the straight line - if this flips, every anatomical constraint
            //    built on top of it is being applied to a mirrored elbow.
            Vec3 forward = new Vec3(0f, 0.45f, 0f);
            Check("forward reach solves",
                VrArmIk.SolveElbowWithBend(shoulder, forward, pole, a, b, out elbow, out ua, out ub));
            Check("elbow hangs below the shoulder-wrist line", elbow.z < -0.01f);

            // 7. Zero-length segments are refused rather than solved. A monster
            //    with a degenerate rest pose must not produce an arm at all.
            Check("zero-length upper arm is refused",
                !VrArmIk.SolveElbowWithBend(shoulder, mid, pole, 0f, b, out elbow, out ua, out ub));
            Check("target on the shoulder is refused",
                !VrArmIk.SolveElbowWithBend(shoulder, shoulder, pole, a, b, out elbow, out ua, out ub));
        }

        /// <summary>
        /// The anatomical constraints on the elbow direction.
        ///
        /// These are checked as PROPERTIES rather than against recorded numbers.
        /// An expected-value test would pin the tuning and have to be rewritten
        /// every time the feel is adjusted; "the elbow never points forward" is
        /// true of every version of this solver that is worth shipping, and stays
        /// true through stage C.
        /// </summary>
        private static void ArmBendConstraints()
        {
            Console.WriteLine();
            Console.WriteLine("Arm bend constraints");

            const float reach = 0.58f;
            Vec3 shoulderR = new Vec3(0.18f, 0f, 1.40f);
            Vec3 shoulderL = new Vec3(-0.18f, 0f, 1.40f);

            // A spread of hand positions covering the workspace: relaxed, forward
            // at chest, forward at face, out to the side, across the body, low and
            // across, overhead, and behind.
            Vec3[] targets =
            {
                new Vec3(0.20f, 0.02f, 0.95f),     // hanging at the side
                new Vec3(0.20f, 0.40f, 1.30f),     // forward, chest height
                new Vec3(0.15f, 0.35f, 1.60f),     // forward, face height
                new Vec3(0.60f, 0.10f, 1.40f),     // out to the side
                new Vec3(-0.15f, 0.35f, 1.35f),    // across the midline
                new Vec3(-0.10f, 0.20f, 1.05f),    // low and across
                new Vec3(0.20f, 0.10f, 1.85f),     // overhead
                new Vec3(0.25f, -0.25f, 1.20f),    // behind
            };

            // Reaching behind yourself is genuinely over-constrained: never-forward
            // and never-above-the-shoulder describe arcs of the elbow circle that do
            // not overlap there, so SOMETHING has to give and the ribs rule is the
            // one documented to give first. It stays in the sweep for the hard rules
            // and is excluded only from the soft one. Measured, not assumed - the
            // two arcs were worked out by hand for this pose.
            const int Behind = 7;

            int forward = 0, flat = 0, notUnit = 0, notPerp = 0, zero = 0;

            for (int ti = 0; ti < targets.Length; ti++)
            {
                Vec3 t = targets[ti];
                for (int side = 0; side < 2; side++)
                {
                    bool isLeft = side == 1;
                    Vec3 shoulder = isLeft ? shoulderL : shoulderR;
                    Vec3 target = isLeft ? new Vec3(-t.x, t.y, t.z) : t;

                    Vec3 bend = Bend(shoulder, target, reach, isLeft);

                    if (bend.LengthSquared < 1e-8f) { zero++; continue; }
                    if (Math.Abs(bend.Length - 1f) > 1e-3f) notUnit++;

                    Vec3 axis = (target - shoulder).NormalizedCopy();
                    if (Math.Abs(Vec3.DotProduct(bend, axis)) > 1e-3f) notPerp++;

                    // The elbow must never point forward. This is the constraint
                    // most likely to be broken by a later blend, because a blend of
                    // two non-forward directions can still land forward.
                    if (Vec3.DotProduct(bend, VrArmBend.Forward) > VrArmBend.ForwardLimit(axis) + 1e-3f) forward++;

                    // And never flat against the ribs: a real arm keeps a few
                    // degrees of abduction even hanging relaxed.
                    Vec3 outward = isLeft ? new Vec3(-1f, 0f, 0f) : new Vec3(1f, 0f, 0f);
                    Vec3 outPerp = VrArmBend.Perp(outward, axis);
                    // TEST 454: the ribs are beside the elbow only at or below the
                    // shoulder; a hand raised above it lets the elbow hang straight down.
                    if (ti != Behind && target.z <= shoulder.z && outPerp.LengthSquared > 1e-8f
                        && Vec3.DotProduct(bend, outPerp) < 0.11f) flat++;
                }
            }

            Check("a direction is produced for every pose", zero == 0);
            Check("every bend direction is unit length", notUnit == 0);
            Check("every bend direction is perpendicular to the arm", notPerp == 0);
            Check("the elbow never points forward past arm_ik_forward_tol (TEST 454)", forward == 0);
            Check("the elbow does not lie flat on the ribs where it can avoid it", flat == 0);

            // Cross-body swing: reaching across should push the elbow further out
            // than the same reach on its own side. Compared as a relative change,
            // so it survives retuning.
            {
                Vec3 own = new Vec3(0.30f, 0.35f, 1.35f);
                Vec3 across = new Vec3(-0.15f, 0.35f, 1.35f);

                Vec3 bOwn = Bend(shoulderR, own, reach, false);
                Vec3 bAcross = Bend(shoulderR, across, reach, false);

                Vec3 outward = new Vec3(1f, 0f, 0f);
                float oOwn = Vec3.DotProduct(bOwn, VrArmBend.Perp(outward, (own - shoulderR).NormalizedCopy()));
                float oAcross = Vec3.DotProduct(bAcross, VrArmBend.Perp(outward, (across - shoulderR).NormalizedCopy()));

                Check("reaching across swings the elbow further out", oAcross > oOwn);
            }

            // The swing is a chest-level aid. Raised to face height it must fade,
            // or the elbow flares into a pose nobody actually holds.
            {
                Vec3 lowAcross = new Vec3(-0.15f, 0.35f, 1.30f);
                Vec3 highAcross = new Vec3(-0.15f, 0.35f, 1.75f);

                Vec3 bLow = Bend(shoulderR, lowAcross, reach, false);
                Vec3 bHigh = Bend(shoulderR, highAcross, reach, false);

                Vec3 outward = new Vec3(1f, 0f, 0f);
                float oLow = Vec3.DotProduct(bLow, VrArmBend.Perp(outward, (lowAcross - shoulderR).NormalizedCopy()));
                float oHigh = Vec3.DotProduct(bHigh, VrArmBend.Perp(outward, (highAcross - shoulderR).NormalizedCopy()));

                Check("the cross-body swing fades above the shoulder", oHigh < oLow);
            }

            // Mirror symmetry. The two arms captured from a real animation would
            // differ; derived analytically they must not, and asymmetric elbows
            // from symmetric input is a fault that is obvious in a headset and
            // very hard to attribute.
            {
                Vec3 tR = new Vec3(0.10f, 0.35f, 1.30f);
                Vec3 tL = new Vec3(-0.10f, 0.35f, 1.30f);

                Vec3 bR = Bend(shoulderR, tR, reach, false);
                Vec3 bL = Bend(shoulderL, tL, reach, true);

                Check("the two arms are mirror images",
                    Math.Abs(bR.x + bL.x) < 1e-3f
                    && Math.Abs(bR.y - bL.y) < 1e-3f
                    && Math.Abs(bR.z - bL.z) < 1e-3f);
            }

            // The height rule, checked on the solved elbow because that is where it
            // actually has to hold.
            //
            // The assertion is "not above the SHOULDER", not "not above the wrist",
            // and the difference is geometry rather than tuning. With the hand
            // hanging at your side the arm is folded, and every point on the elbow's
            // circle then sits between shoulder and wrist - so "below the wrist" is
            // unreachable there, and correctly so: a bent arm at your side really
            // does have its elbow above its wrist. The solver asks for the lower of
            // the two and takes the lowest the circle allows when it cannot have it.
            // Asserting the unreachable version would be asserting a bug.
            {
                int above = 0;
                Vec3 shoulder = shoulderR;

                Vec3[] low =
                {
                    new Vec3(0.20f, 0.02f, 0.95f),
                    new Vec3(0.20f, 0.40f, 1.30f),
                    new Vec3(0.45f, 0.30f, 1.20f),
                    new Vec3(-0.10f, 0.25f, 1.10f),
                };

                foreach (Vec3 t in low)
                {
                    Vec3 elbow;
                    float ua, ub;
                    if (!VrArmIk.SolveElbow(shoulder, t, 0.30f, 0.28f, false, 1f,
                                            out elbow, out ua, out ub))
                        continue;

                    if (elbow.z > shoulder.z + 1e-3f) above++;
                }

                Check("a low hand never lifts the elbow above the shoulder", above == 0);
            }

            // And where the cap IS reachable, it must be met exactly rather than
            // approached - this is the pose that proves the swivel is being solved
            // and not merely nudged.
            {
                Vec3 shoulder = shoulderR;
                Vec3 t = new Vec3(0.20f, 0.40f, 1.30f);

                Vec3 elbow;
                float ua, ub;
                Check("reachable-cap pose solves",
                    VrArmIk.SolveElbow(shoulder, t, 0.30f, 0.28f, false, 1f,
                                       out elbow, out ua, out ub));
                Check("the elbow is brought to or below the wrist when it can be",
                    elbow.z <= t.z + 2e-3f);
            }

            // The cap must not move the elbow off its circle - the whole reason it
            // swivels the direction instead of displacing the joint. If this fails,
            // the arm is visibly changing length as the hand moves.
            {
                Vec3 shoulder = shoulderR;
                Vec3 t = new Vec3(0.45f, 0.30f, 1.15f);

                Vec3 elbow;
                float ua, ub;
                Check("capped pose solves",
                    VrArmIk.SolveElbow(shoulder, t, 0.30f, 0.28f, false, 1f,
                                       out elbow, out ua, out ub));
                Approx("the cap preserves the upper arm length", (elbow - shoulder).Length, ua);
                Approx("the cap preserves the forearm length", (t - elbow).Length, ub);
            }
        }

        /// <summary>
        /// The leg solve: the same triangle as the arm, its own pole, and the
        /// engine's animated feet as the target.
        ///
        /// Three properties matter and each one is a way the leg can be visibly
        /// wrong rather than subtly wrong: the segments must connect the hip to
        /// the ankle (a length error is a foot through the floor), the knee must
        /// stay on its circle (a displacement changes the leg's apparent length),
        /// and a crouching pelvis must move the knee FORWARD (a hyperextended leg
        /// at the exact moment the player squats).
        /// </summary>
        private static void LegIkGeometry()
        {
            Section("leg IK geometry");

            // A standing leg: hip at 0.9 m, ankle at 0.1 m, a real pair of lengths.
            var hip = new Vec3(0f, 0f, 0.90f, -1f);
            var ankle = new Vec3(0f, 0f, 0.10f, -1f);
            var toeDir = new Vec3(0f, 1f, 0f, -1f);
            const float Thigh = 0.42f;
            const float Calf = 0.40f;

            Vec3 knee, thighDir, calfDir;

            // The animated knee lies on the chord for a straight leg, so this is
            // also the fallback path (the toe names the plane).
            Vec3 onChord = hip + (ankle - hip) * 0.5f;
            Check("a straight leg solves",
                VrLegIk.Solve(hip, ankle, onChord, toeDir, Thigh, Calf,
                              out knee, out thighDir, out calfDir));

            Approx("thigh length is preserved", (knee - hip).Length, Thigh);
            Approx("shin length is preserved", (ankle - knee).Length, Calf);
            Check("the straight-leg knee falls forward", knee.y > ankle.y + 0.01f);

            // A hint that names a knee BEHIND the chord: a real knee does not do
            // that, and the leg must not follow the hint off a cliff.
            Vec3 backwards = hip + (ankle - hip) * 0.5f + new Vec3(0f, -0.30f, 0f, -1f);
            Check("a backwards hint still solves",
                VrLegIk.Solve(hip, ankle, backwards, toeDir, Thigh, Calf,
                              out knee, out thighDir, out calfDir));
            Check("the knee never bends backwards", knee.y > ankle.y - 1e-3f);

            // A crouch: the pelvis drops 0.2 m, the ankle does not move. The knee
            // has to come forward, which is the whole reason the leg is solved at
            // all rather than left to the animation.
            var crouchHip = new Vec3(0f, 0f, 0.70f, -1f);
            Vec3 straightKnee;
            Check("the standing knee is available for comparison",
                VrLegIk.Solve(hip, ankle, onChord, toeDir, Thigh, Calf,
                              out straightKnee, out thighDir, out calfDir));
            Check("a crouch solves",
                VrLegIk.Solve(crouchHip, ankle, onChord, toeDir, Thigh, Calf,
                              out knee, out thighDir, out calfDir));
            Check("a crouch bends the knee forward", knee.y > straightKnee.y + 0.05f);
            Approx("the crouch keeps the ankle put", (ankle - knee).Length, Calf);

            // A hip further from the ankle than the leg is long: the arm's stretch
            // path is reused, so the segments lengthen rather than refusing.
            var reachHip = new Vec3(0f, 0f, 1.10f, -1f);
            Check("an over-reach solves",
                VrLegIk.Solve(reachHip, ankle, onChord, toeDir, Thigh, Calf,
                              out knee, out thighDir, out calfDir));
            Approx("an over-reach reaches the ankle", (ankle - knee).Length + (knee - reachHip).Length,
                   (ankle - reachHip).Length);
        }

        /// <summary>
        /// The room-scale control law, as a pure function: the dead zone, the
        /// position servo, the feed-forward, the cap, and the consumption clamp.
        ///
        /// These are the numbers that decide whether a physical step walks the
        /// character at one step or at two, so they are asserted rather than
        /// tuned by feel in a headset - where a wrong answer is not a wrong
        /// number but a world that slides under your feet.
        /// </summary>
        private static void RoomscaleControl()
        {
            Section("room scale control");

            // Inside the dead zone, standing still: silent.
            Vec2 idle = VrRoomscale.Command(new Vec3(0.02f, 0.01f, 0f, -1f), Vec3.Zero);
            Check("the dead zone is quiet", idle.LengthSquared < 1e-8f);

            // 0.25 m ahead with the head still: kp * (0.25 - 0.05) / speed.
            // The defaults the source ships: kp 3.0, speed 2.0.
            Vec2 servo = VrRoomscale.Command(new Vec3(0f, 0.25f, 0f, -1f), Vec3.Zero);
            Approx("the servo walks forward", servo.y, 3.0f * 0.20f / 2.0f);
            Approx("the servo adds nothing sideways", servo.x, 0f);

            // Walking forward: the feed-forward (kd 1.2) passes the player's own
            // speed through on top of the servo, up to the 0.9 cap.
            Vec2 walking = VrRoomscale.Command(new Vec3(0f, 0.25f, 0f, -1f),
                                               new Vec3(0f, 1.0f, 0f, -1f));
            Approx("the feed-forward adds the player's speed", walking.y,
                   (3.0f * 0.20f + 1.2f * 1.0f) / 2.0f);

            // Walking back toward the body must not read as a command to walk
            // backwards: it closes the offset, and the servo is all that is left.
            Vec2 closing = VrRoomscale.Command(new Vec3(0f, 0.25f, 0f, -1f),
                                               new Vec3(0f, -1.0f, 0f, -1f));
            Approx("closing motion is not commanded", closing.y, 3.0f * 0.20f / 2.0f);

            // The cap, and the sideways direction.
            Vec2 capped = VrRoomscale.Command(new Vec3(0f, 3.0f, 0f, -1f),
                                              new Vec3(0f, 4.0f, 0f, -1f));
            Check("the input is capped", capped.y <= 0.9f + 1e-4f);

            Vec2 sideways = VrRoomscale.Command(new Vec3(0.25f, 0f, 0f, -1f), Vec3.Zero);
            Check("sideways offset commands a sideways step", sideways.x > 0.2f && Math.Abs(sideways.y) < 1e-4f);

            // Consumption: only along the offset, never negative, never more than
            // the offset itself.
            var offset = new Vec3(0f, 0.5f, 0f, -1f);
            Approx("movement along the offset is consumed",
                   VrRoomscale.Consumed(offset, new Vec3(0f, 0.2f, 0f, -1f)), 0.2f);
            Approx("movement across it is not",
                   VrRoomscale.Consumed(offset, new Vec3(0.3f, 0f, 0f, -1f)), 0f);
            Approx("movement against it is not",
                   VrRoomscale.Consumed(offset, new Vec3(0f, -0.3f, 0f, -1f)), 0f);
            Approx("no more than the offset is consumed",
                   VrRoomscale.Consumed(offset, new Vec3(0f, 9f, 0f, -1f)), 0.5f);

            // A diagonal step: the consumed share is the projection of the step
            // onto the offset direction, which for a parallel step is its length.
            var diagonal = new Vec3(0.4f, 0.4f, 0f, -1f);
            float projected = VrRoomscale.Consumed(diagonal, new Vec3(0.1f, 0.1f, 0f, -1f));
            Approx("a diagonal step consumes its projection", projected, 0.141421f);
        }

        /// <summary>
        /// What the engine's frame helpers do when the rotation carries SCALE.
        ///
        /// This is not a hypothetical. The player agent measured in game has an
        /// entity scale of 1.0615, and the whole hand pipeline is conversions in and
        /// out of that frame. A helper that is only correct for an orthonormal
        /// rotation is then wrong by a few centimetres at the hand - which is
        /// exactly the size of error left over after the obvious one was fixed.
        ///
        /// Asserted against the shipped assemblies rather than reasoned about,
        /// because none of it is documented and the names do not settle it.
        /// </summary>
        private static void ScaledFrameRoundTrip()
        {
            Section("scaled frame round trip");

            const float Scale = 1.0615f;

            // A realistic parent: yawed, raised, and uniformly scaled the way an
            // agent entity's global frame is.
            Mat3 rot = default;
            rot.s = new Vec3(0f, -1f, 0f, -1f);
            rot.f = new Vec3(1f, 0f, 0f, -1f);
            rot.u = new Vec3(0f, 0f, 1f, -1f);
            rot.ApplyScaleLocal(Scale);

            MatrixFrame parent = default;
            parent.rotation = rot;
            parent.origin = new Vec3(12f, -4f, 7f, -1f);

            Check("the parent really is scaled", parent.rotation.HasScale());
            Approx("and by the amount intended", parent.rotation.GetScaleVector().x, Scale);

            // A child somewhere plausible for a hand: about a metre up and half a
            // metre out from the parent's origin.
            MatrixFrame child = default;
            child.rotation = Mat3.Identity;
            child.origin = new Vec3(12.5f, -4.3f, 8.4f, -1f);

            // 1. The transpose is NOT the inverse here. This is the bug that put
            //    the hand 28 cm high; it is asserted so it cannot come back.
            {
                MatrixFrame local = parent.TransformToLocal(child);
                MatrixFrame back = parent.TransformToParent(local);
                float err = (back.origin - child.origin).Length;

                Check("TransformToLocal does NOT round trip on a scaled frame",
                    err > 1e-3f);
                Console.WriteLine(string.Format(
                    "    transpose round-trip error {0:F4} m (expected: large)", err));
            }

            // 2. The non-orthogonal form uses a real inverse and must round trip.
            //    If THIS fails, the remaining centimetres are here.
            {
                MatrixFrame local = parent.TransformToLocalNonOrthogonal(child);
                MatrixFrame back = parent.TransformToParent(local);
                float err = (back.origin - child.origin).Length;

                Check("TransformToLocalNonOrthogonal DOES round trip", err < 1e-3f);
                Console.WriteLine(string.Format(
                    "    inverse round-trip error {0:F6} m (expected: ~0)", err));
            }

            // 3. The chain the arm solve actually walks: parent -> helper -> child,
            //    where the helper's local frame is read from the animation and
            //    recomposed onto a moved parent. Every step must survive scale, and
            //    a constant error here is a constant error at the wrist.
            {
                MatrixFrame helper = default;
                helper.rotation = Mat3.Identity;
                helper.origin = new Vec3(12.2f, -4.1f, 7.9f, -1f);

                MatrixFrame helperLocal = parent.TransformToLocalNonOrthogonal(helper);
                MatrixFrame childLocal = helper.TransformToLocalNonOrthogonal(child);

                MatrixFrame helperBack = parent.TransformToParent(helperLocal);
                MatrixFrame childBack = helperBack.TransformToParent(childLocal);

                float err = (childBack.origin - child.origin).Length;
                Check("a two-step recompose survives scale", err < 1e-3f);
                Console.WriteLine(string.Format(
                    "    two-step recompose error {0:F6} m (expected: ~0)", err));
            }

            // 4. Orthonormalising a scaled rotation must recover the rotation
            //    itself, not some other one. The hand's orientation is written
            //    through exactly this path.
            {
                Mat3 m = rot;
                m.Orthonormalize();

                Vec3 v = new Vec3(0.3f, -0.7f, 0.6f, -1f);
                Vec3 byUnit = m.TransformToParent(v);
                Vec3 byScaled = rot.TransformToParent(v);

                Expect("orthonormalize recovers the rotation",
                    byUnit, new Vec3(byScaled.x / Scale, byScaled.y / Scale,
                                     byScaled.z / Scale, -1f));
            }
        }

        /// <summary>
        /// The bend-direction filter. Small, but two of its properties are the kind
        /// that fail silently for weeks.
        /// </summary>
        private static void ArmBendSmoothing()
        {
            Console.WriteLine();
            Console.WriteLine("Arm bend smoothing");

            Vec3 axis = new Vec3(0f, 1f, 0f);            // arm pointing forward
            Vec3 from = new Vec3(0f, 0f, -1f);           // elbow down
            Vec3 to = new Vec3(1f, 0f, 0f);              // elbow out to the side

            // With no previous value the filter must pass the target through
            // untouched, or the first solved frame of every mission eases in from
            // nowhere and the arm visibly swings into place.
            Vec3 first = VrArmBend.Smooth(Vec3.Zero, false, to, axis, 1f / 90f, 0.08f, 500f);
            Check("with no history the target passes straight through",
                (first - to).Length < 1e-4f);

            // The result must stay ON the elbow's circle. Lerping two unit vectors
            // does not, and the cosine-law placement assumes it does - so skipping
            // the re-projection makes the arm change length as the elbow turns,
            // which reads as a rubber forearm rather than as a filter bug.
            Vec3 step = VrArmBend.Smooth(from, true, to, axis, 1f / 90f, 0.08f, 500f);
            Approx("the eased direction stays unit length", step.Length, 1f);
            Check("the eased direction stays perpendicular to the arm",
                Math.Abs(Vec3.DotProduct(step, axis)) < 1e-3f);

            // It has to actually move, and not past the target.
            Check("easing moves toward the target",
                Vec3.DotProduct(step, to) > Vec3.DotProduct(from, to));
            Check("easing does not overshoot", Vec3.DotProduct(step, to) <= 1f + 1e-4f);

            // The rate cap is the thing that stops a large honest swing landing in
            // one frame. At 90 Hz and 500 deg/s a step cannot exceed ~5.6 degrees.
            {
                Vec3 capped = VrArmBend.Smooth(from, true, to, axis, 1f / 90f, 0.001f, 500f);
                float moved = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f,
                    Vec3.DotProduct(from, capped)))) * 180.0 / Math.PI);
                Check("the rate cap bounds a single step", moved <= 500f / 90f + 0.5f);
            }

            // A frame-time spike must not be allowed to teleport the elbow.
            {
                Vec3 hitch = VrArmBend.Smooth(from, true, to, axis, 2.0f, 0.08f, 500f);
                float moved = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f,
                    Vec3.DotProduct(from, hitch)))) * 180.0 / Math.PI);
                Check("a long frame does not teleport the elbow", moved < 90f);
            }

            // Exactly antiparallel: the interpolation degenerates to zero length and
            // the filter must snap rather than return something unnormalisable.
            {
                Vec3 opposite = -from;
                Vec3 flipped = VrArmBend.Smooth(from, true, opposite, axis, 1f / 90f, 0.08f, 500f);
                Approx("an antiparallel target still yields a unit direction",
                    flipped.Length, 1f);
            }

            // Repeated easing toward a fixed target must converge, not orbit.
            {
                Vec3 cur = from;
                for (int i = 0; i < 400; i++)
                    cur = VrArmBend.Smooth(cur, true, to, axis, 1f / 90f, 0.08f, 500f);

                Check("easing converges on the target", Vec3.DotProduct(cur, to) > 0.999f);
            }
        }

        /// <summary>
        /// The bend direction the shipped solver actually used, recovered from the
        /// elbow it produced. Going through the real solve rather than calling the
        /// direction function with hand-computed angles means these checks cannot
        /// pass while the code path the game runs is broken.
        /// </summary>
        /// <summary>
        /// TEST 270: the wrist's roll, and the elbow following it.
        ///
        /// WristTwist has to read back exactly the roll put into the hand, or the
        /// forearm helper twists the wrong amount; and swinging the elbow by
        /// ElbowRoll has to REDUCE the roll left at the wrist, or the sign is wrong
        /// and the elbow flares the opposite way to a real one.
        /// </summary>
        private static void ArmTwist()
        {
            Section("arm twist (TEST 270)");

            Vec3 shoulder = new Vec3(0.18f, 0f, 1.40f);
            Vec3 target = new Vec3(0.25f, 0.35f, 1.15f);
            float limit = 85f * (float)(Math.PI / 180.0);

            Vec3 elbow, bend;
            float ua, ub;
            Check("twist base pose solves",
                VrArmIk.SolveElbow(shoulder, target, 0.30f, 0.28f, false, 1f,
                                   out elbow, out ua, out ub, out bend));

            Vec3 fore = (target - elbow).NormalizedCopy();
            Vec3 normal = Vec3.CrossProduct((elbow - shoulder).NormalizedCopy(), fore).NormalizedCopy();

            // The neutral hand is the identity, so its hinge axis in hand-local is
            // the normal itself.
            Mat3 neutral = Mat3.Identity;
            Approx("neutral hand reads zero roll",
                   VrArmBend.WristTwist(neutral, normal, fore, normal), 0f);

            foreach (float deg in new[] { 30f, 60f, -60f })
            {
                float rad = deg * (float)(Math.PI / 180.0);
                Approx("hand rolled " + deg + " deg reads back",
                       VrArmBend.WristTwist(RotAbout(neutral, fore, rad), normal, fore, normal), rad);
            }

            Approx("roll inside the range: a share", VrArmIk.ElbowRoll(0.5f, limit, 0.3f), 0.15f);
            Approx("roll past the range: all of the excess",
                   VrArmIk.ElbowRoll(limit + 0.4f, limit, 0.3f), 0.3f * limit + 0.4f);

            // The swing takes roll off the wrist, for either direction the elbow is
            // free to go. Rules may pin one side, so one side is required to move.
            int reduced = 0;
            foreach (float deg in new[] { 60f, -60f, 120f, -120f })
            {
                float rad = deg * (float)(Math.PI / 180.0);
                Mat3 hand = RotAbout(neutral, fore, rad);

                float before = VrArmBend.WristTwist(hand, normal, fore, normal);
                float roll = VrArmIk.ElbowRoll(before, limit, 0.3f);

                Vec3 e2, b2;
                if (!VrArmIk.SolveElbow(shoulder, target, 0.30f, 0.28f, false, 1f,
                                        out e2, out ua, out ub, out b2, roll))
                {
                    Fail("rolled pose solves at " + deg);
                    continue;
                }

                Vec3 f2 = (target - e2).NormalizedCopy();
                Vec3 n2 = Vec3.CrossProduct((e2 - shoulder).NormalizedCopy(), f2).NormalizedCopy();
                float after = VrArmBend.WristTwist(hand, normal, f2, n2);

                Check(string.Format("elbow swing never adds wrist roll ({0} deg: {1:F1} -> {2:F1})",
                                    deg, before * 57.3f, after * 57.3f),
                      Math.Abs(after) <= Math.Abs(before) + 1e-3f);
                if (Math.Abs(after) < Math.Abs(before) - 0.05f)
                    reduced++;

                Approx("rolled solve keeps the upper arm at " + deg, (e2 - shoulder).Length, ua);
                Approx("rolled solve keeps the forearm at " + deg, (target - e2).Length, ub);
                Check("rolled solve never bends the elbow forward at " + deg,
                      Vec3.DotProduct(b2, VrArmBend.Forward) <= 1e-3f);
            }

            Check("the elbow swing takes roll off the wrist in at least one direction", reduced > 0);

            // TEST 289: the hinge from the rest pose.
            bool fromBend;
            Vec3 shR = new Vec3(0.18f, 0f, 1.40f), elR = new Vec3(0.48f, 0f, 1.40f), wrR = new Vec3(0.76f, 0f, 1.40f);
            Expect("straight right T-pose flexes forward: +Z",
                   VrArmBend.RestHinge(shR, elR, wrR, VrArmBend.Forward, true, out fromBend), new Vec3(0f, 0f, 1f));
            Check("a straight rest arm does not count as a bend", !fromBend);

            Vec3 shL = new Vec3(-0.18f, 0f, 1.40f), elL = new Vec3(-0.48f, 0f, 1.40f), wrL = new Vec3(-0.76f, 0f, 1.40f);
            Expect("straight left T-pose mirrors: -Z",
                   VrArmBend.RestHinge(shL, elL, wrL, VrArmBend.Forward, true, out fromBend), new Vec3(0f, 0f, -1f));

            Vec3 bentWrist = new Vec3(0.76f, 0.10f, 1.40f);
            VrArmBend.RestHinge(shR, elR, bentWrist, VrArmBend.Up, true, out fromBend);
            Check("auto takes a real rest bend over the flex direction", fromBend);
            Vec3 forced = VrArmBend.RestHinge(shR, elR, bentWrist, VrArmBend.Forward, false, out fromBend);
            Check("a forced flex direction ignores the rest bend", !fromBend && forced.z > 0.99f);

            // Palm-down T-pose, identity bone frames: the hand's hinge axis is +Z.
            // Pointed forward, elbow down, thumb up - the neutral wrist - must read 0.
            Mat3 thumbUp = default(Mat3);
            thumbUp.s = new Vec3(0f, 1f, 0f);   // along the arm: forward
            thumbUp.f = new Vec3(0f, 0f, 1f);   // thumb: up
            thumbUp.u = new Vec3(1f, 0f, 0f);   // back of the hand: out to the right
            Vec3 foreFwd = new Vec3(0f, 1f, 0f);
            Vec3 hingeDown = Vec3.CrossProduct(new Vec3(0f, 0f, -1f), foreFwd);
            Approx("palm-down bind reads a thumb-up wrist as neutral",
                   VrArmBend.WristTwist(thumbUp, new Vec3(0f, 0f, 1f), foreFwd, hingeDown), 0f);
            Approx("and palm-down from there as a quarter turn",
                   Math.Abs(VrArmBend.WristTwist(RotAbout(thumbUp, foreFwd, 1.5708f), new Vec3(0f, 0f, 1f),
                                                 foreFwd, hingeDown)), 1.5708f);
        }

        private static Mat3 RotAbout(Mat3 m, Vec3 axis, float rad)
        {
            float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
            Func<Vec3, Vec3> r = v => v * c + Vec3.CrossProduct(axis, v) * s
                                      + axis * (Vec3.DotProduct(axis, v) * (1f - c));
            Mat3 o = m;
            o.s = r(m.s);
            o.f = r(m.f);
            o.u = r(m.u);
            return o;
        }

        private static Vec3 Bend(Vec3 shoulder, Vec3 target, float reach, bool isLeft)
        {
            Vec3 elbow;
            float ua, ub;
            if (!VrArmIk.SolveElbow(shoulder, target, 0.30f, 0.28f, isLeft, 1f,
                                    out elbow, out ua, out ub))
                return Vec3.Zero;

            Vec3 axis = (target - shoulder).NormalizedCopy();
            return VrArmBend.Perp(elbow - shoulder, axis);
        }

        /// <summary>TEST 290 / 291: the twist fade, the archer's elbows, and the
        /// body-turn deadzone.</summary>
        private static void ArcheryArmsAndBodyTurn()
        {
            Section("archery arms and body turn (TEST 290/291)");

            const float Deg = (float)(Math.PI / 180.0);
            float limit = 85f * Deg;

            // The wrap: +179.5 and -179.5 are one hand pose, so the roll acted on
            // must agree - it used to differ by ~240 degrees.
            float rollPlus = VrArmIk.ElbowRoll(179.5f * Deg, limit, 0.3f) * VrArmBend.TwistWeight(179.5f * Deg, 1f);
            float rollMinus = VrArmIk.ElbowRoll(-179.5f * Deg, limit, 0.3f) * VrArmBend.TwistWeight(-179.5f * Deg, 1f);
            Check(string.Format("roll is continuous across the +-180 wrap ({0:F3} vs {1:F3} rad)", rollPlus, rollMinus),
                  Math.Abs(rollPlus - rollMinus) < 0.05f);
            Approx("twist weight at neutral, clear axis", VrArmBend.TwistWeight(0f, 1f), 1f);
            Approx("twist weight with the axis along the forearm", VrArmBend.TwistWeight(0.5f, 0.05f), 0f);
            Approx("twist weight inside the range", VrArmBend.TwistWeight(90f * Deg, 1f), 1f);

            // String arm, right side: the hand at the mouth - in front of and above
            // the shoulder, and inboard of it. TEST 290b: the screenshots showed the
            // upper arm swung up across the face, because "back" projected onto an
            // arm axis that points forward and up is "up". The elbow belongs out to
            // the right.
            Vec3 sR = new Vec3(0.18f, 0f, 1.40f);
            float w;
            Vec3 plain, drawn, bend;
            float ua, ub;
            Vec3[] mouths =
            {
                new Vec3(0.00f, 0.20f, 1.58f),
                new Vec3(0.05f, 0.10f, 1.55f),
                new Vec3(-0.02f, 0.30f, 1.60f),
            };
            foreach (Vec3 jaw in mouths)
            {
                Vec3 pole = VrArmIk.BowPole(ArmRole.String, false, sR, jaw, 0.58f, out w);
                Approx("string pole fully on with the hand above the shoulder", w, 1f);
                Check("mouth pose solves with the archer's pole",
                      VrArmIk.SolveElbow(sR, jaw, 0.30f, 0.28f, false, 1f, out drawn, out ua, out ub, out bend,
                                         0f, pole, w));
                VrArmIk.SolveElbow(sR, jaw, 0.30f, 0.28f, false, 1f, out plain, out ua, out ub);
                string at = string.Format("({0:F2},{1:F2},{2:F2}), plain ({3:F2},{4:F2},{5:F2})",
                                          drawn.x, drawn.y, drawn.z, plain.x, plain.y, plain.z);
                Check("draw elbow out to the right of the shoulder " + at, drawn.x > sR.x + 0.05f);
                Check("draw elbow not in front of the mouth " + at, drawn.y < jaw.y);
                // The eye sits over the midline; the upper arm has to clear it.
                Check("draw elbow at least 25 cm right of the face " + at, drawn.x - jaw.x > 0.25f);
            }

            // Reaching low with a bow out is not a draw: the pole stays off.
            VrArmIk.BowPole(ArmRole.String, false, sR, new Vec3(0.25f, 0.15f, 1.05f), 0.58f, out w);
            Approx("string pole off with the hand at the hip", w, 0f);

            // Bow arm, left side: nearly straight ahead, elbow rotated out.
            Vec3 sL = new Vec3(-0.18f, 0f, 1.40f);
            Vec3 bowHand = new Vec3(-0.08f, 0.52f, 1.50f);
            Vec3 bowPole = VrArmIk.BowPole(ArmRole.Bow, true, sL, bowHand, 0.58f, out w);
            Vec3 bowElbow;
            Check("bow arm solves",
                  VrArmIk.SolveElbow(sL, bowHand, 0.30f, 0.28f, true, 1f, out bowElbow, out ua, out ub, out bend,
                                     0f, bowPole, w));
            float midX = (sL.x + bowHand.x) * 0.5f;
            Check(string.Format("bow elbow is rotated outward (x {0:F3} vs line {1:F3})", bowElbow.x, midX),
                  bowElbow.x < midX);

            // The body-turn deadzone. dir(a) is a heading a radians to the left.
            Func<float, Vec2> dir = a => new Vec2(-(float)Math.Sin(a), (float)Math.Cos(a));
            Func<Vec2, Vec2, float> between = (a, b) =>
                (float)Math.Atan2(a.x * b.y - a.y * b.x, a.x * b.x + a.y * b.y);

            Vec2 body = dir(0f);
            Vec2 held = VrHeadAim.Deadzone(body, dir(60f * Deg), 90f * Deg);
            Approx("head 60 deg left: body stays", between(body, held), 0f);

            held = VrHeadAim.Deadzone(body, dir(120f * Deg), 90f * Deg);
            Approx("head 120 deg left: body turns 30", between(body, held) / Deg, 30f);

            held = VrHeadAim.Deadzone(body, dir(-150f * Deg), 90f * Deg);
            Approx("head 150 deg right: body turns 60 right", between(body, held) / Deg, -60f);

            held = VrHeadAim.Deadzone(body, dir(40f * Deg), 0f);
            Approx("a zero deadzone follows the head", between(body, held) / Deg, 40f);

            // TEST 296: room-scale delivery. A step straight ahead of the body, with
            // the head turned 90 deg left, is the head's RIGHT.
            Vec2 d = VrRoomscale.Deliver(new Vec2(0f, 0.5f), 90f * Deg, false, 0.3f);
            Approx("room step ahead, head 90 left: head's right (x)", d.x, 0.5f);
            Approx("room step ahead, head 90 left: head's right (y)", d.y, 0f);

            d = VrRoomscale.Deliver(new Vec2(0.1f, 0f), 0f, true, 0.3f);
            Approx("a small step is lifted past the engine deadzone", d.x, 0.37f);
            d = VrRoomscale.Deliver(new Vec2(0.05f, 0f), 0f, false, 0.3f);
            Approx("head sway alone is not lifted", d.x, 0.05f);
            d = VrRoomscale.Deliver(Vec2.Zero, 0.3f, true, 0.3f);
            Check("no command stays no command", d.x == 0f && d.y == 0f);
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
