using System;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Managed half of the view-projection patcher: publishes, once per frame,
    /// the eye the engine should be rendering from and enough of the engine's own
    /// camera to recognise its matrix among all the others.
    ///
    /// WHY THE CAMERA IS NOT TOUCHED AT ALL ANY MORE
    /// ---------------------------------------------
    /// Four routes into the engine's camera were built and measured:
    ///
    ///   CustomCamera, new camera per frame .. geometry breaks
    ///   CustomCamera, one camera bound once .. sky only
    ///   move CombatCamera in place .......... geometry perfect, but
    ///                                         "Engine FOV hold: 450 correction(s)
    ///                                          in the last ~5 s" - at 90 fps that
    ///                                         is the engine overwriting us on
    ///                                         every single frame, pose included
    ///   don't touch the camera .............. geometry perfect, camera not ours
    ///
    /// Every route that renders correctly renders from the engine's camera, and
    /// every route that injects ours breaks the scene. The engine owns
    /// CombatCamera outright, so the mod stops competing for it: route four is
    /// the one we keep, and the head pose is applied downstream instead, in the
    /// constant buffer, where the engine has no opportunity to write it back.
    ///
    /// WHAT THIS CLASS IS CAREFUL ABOUT
    /// --------------------------------
    /// It publishes the engine camera's position and view direction, and NOTHING
    /// derived from Camera.Frame's basis. Camera.Frame does not use the
    /// convention it appears to - CreateLookAt for a level view along +Y returns
    /// f = world up - and this file has no way to be sure which axis is which.
    /// Direction is unambiguous, position is a point, and the native side
    /// recovers the rest from the matrix itself, where it is unambiguous too.
    /// </summary>
    public static class VrVpPatch
    {
        private static float _pubErrSum;
        private static float _pubErrMax;
        private static int   _pubErrCount;
        private static int   _pubLogTick;

        /// <summary>
        /// PUBLISHING IS NEEDED FOR MEASUREMENT TOO, NOT ONLY FOR SUBSTITUTION.
        ///
        /// This used to read vp_patch alone, and that is what made vp_measure = 1
        /// do nothing at all. The native scan opens with read_published() and
        /// returns immediately when there is no frame - it needs the engine
        /// camera's position and direction to recognise which of the many
        /// matrices flowing past is the main view. No publication, no recognition,
        /// no frustum measured; the native log said so as "scanned 0, no
        /// publication 179507" and submitted_fov quietly fell back to the
        /// computed frustum, which is the 0.650 head-to-world gain.
        ///
        /// So the gate is either job. The native side decides separately whether
        /// to WRITE anything - see Settings::measure in vp_patch.cpp - and under
        /// vp_measure it writes nothing, so publishing here costs a matrix upload
        /// a frame and changes not one pixel by itself.
        /// </summary>
        public static bool Enabled { get; } =
            VrConfig.Bool("vp_patch", false) || VrConfig.Bool("vp_measure", false);

        /// <summary>
        /// True only when the native side actually REWRITES matrices.
        ///
        /// Separate from Enabled because the two imply opposite things about the
        /// camera. Substitution applies the head pose downstream of the camera, so
        /// the camera must be left alone or the pose lands twice. Measurement
        /// writes nothing, so the camera must still be driven exactly as it was.
        /// Anything choosing a CAMERA behaviour wants this; anything asking
        /// "should I publish" wants Enabled.
        /// </summary>
        public static bool Substituting { get; } = VrConfig.Bool("vp_patch", false);

        /// <summary>Layout mirrors BVR_VP_FRAME_FLOATS in bvr_api.h.</summary>
        private const int FloatCount = 26;

        private static readonly float[] Data = new float[FloatCount];

        private static bool _logged;
        private static bool _standDown;
        private static int _failures;

        /// <summary>
        /// Called from the AFR tick, after VrCameraDriver.Update has refreshed the
        /// eye frames for this frame and before the engine renders.
        /// </summary>
        /// <summary>
        /// The other eye, into its own native slot.
        ///
        /// A second buffer rather than reusing Data, because Data is still the
        /// array the first publication was marshalled from - overwriting it here
        /// would be harmless today only because the call above has already
        /// returned, and that is exactly the kind of thing that stops being true
        /// when someone makes a call asynchronous.
        /// </summary>
        private static readonly float[] OtherData = new float[FloatCount];

        private static void PublishOther(int eye, Camera combat)
        {
            if (eye < 0 || eye > 1 || combat == null)
                return;

            MatrixFrame f = VrCameraDriver.EyeFrame(eye);

            OtherData[0] = f.rotation.s.x; OtherData[1] = f.rotation.s.y; OtherData[2] = f.rotation.s.z; OtherData[3] = 0f;
            OtherData[4] = f.rotation.u.x; OtherData[5] = f.rotation.u.y; OtherData[6] = f.rotation.u.z; OtherData[7] = 0f;
            OtherData[8] = f.rotation.f.x; OtherData[9] = f.rotation.f.y; OtherData[10] = f.rotation.f.z; OtherData[11] = 0f;
            OtherData[12] = f.origin.x; OtherData[13] = f.origin.y; OtherData[14] = f.origin.z; OtherData[15] = 1f;

            // The reference camera is the ENGINE's, and it is the same for both
            // eyes - it is what the native scan recognises the main view by, not
            // anything the replacement is built from.
            MatrixFrame engineFrame = combat.Frame;
            OtherData[16] = engineFrame.origin.x;
            OtherData[17] = engineFrame.origin.y;
            OtherData[18] = engineFrame.origin.z;

            Vec3 dir = combat.Direction;
            OtherData[19] = dir.x;
            OtherData[20] = dir.y;
            OtherData[21] = dir.z;

            VrCameraDriver.EyeTangents(eye, out float tl, out float tr, out float tu, out float td);
            OtherData[22] = tl;
            OtherData[23] = tr;
            OtherData[24] = tu;
            OtherData[25] = td;

            NativeMethods.bvr_set_vp_frame_other(OtherData, FloatCount, eye, 1);
        }

        public static void Publish(MissionScreen screen, int eye)
        {
            if (!Enabled || _standDown || screen == null || eye < 0 || eye > 1)
                return;

            if (!VrCameraDriver.HaveEyeFrames)
                return;

            Camera combat = screen.CombatCamera;
            if (combat == null)
                return;

            try
            {
                MatrixFrame f = VrCameraDriver.EyeFrame(eye);

                // Row-major, row-vector (v * M): rows 0,1,2 are the local axes and
                // row 3 is the position. The native side builds the engine's frame
                // with the SAME labelling, and the labelling cancels out of the
                // delta - see the derivation at the top of vp_patch.cpp. What must
                // not happen is the two sides disagreeing, which is exactly why
                // both are written down explicitly rather than inferred.
                Data[0] = f.rotation.s.x; Data[1] = f.rotation.s.y; Data[2] = f.rotation.s.z; Data[3] = 0f;
                Data[4] = f.rotation.u.x; Data[5] = f.rotation.u.y; Data[6] = f.rotation.u.z; Data[7] = 0f;
                Data[8] = f.rotation.f.x; Data[9] = f.rotation.f.y; Data[10] = f.rotation.f.z; Data[11] = 0f;
                Data[12] = f.origin.x; Data[13] = f.origin.y; Data[14] = f.origin.z; Data[15] = 1f;

                MatrixFrame engineFrame = combat.Frame;
                Data[16] = engineFrame.origin.x;
                Data[17] = engineFrame.origin.y;
                Data[18] = engineFrame.origin.z;

                // D, MEASURED IN MANAGED, AT THE LINE THAT PRODUCES IT.
                //
                // Native computes D = eyeCamera - refPos from exactly these two
                // values, and reports ~250 mm on the LIVE view with the head
                // held still. AimAtEye's read-back reports 0.0 mm, and since
                // TEST 95 AimAtEye is the LAST thing to touch the camera before
                // this line - so both cannot be true and one of them is not
                // measuring what it claims.
                //
                // This is the same two numbers, differenced HERE. If it reads
                // near zero, the 250 mm is created downstream of this point and
                // the fault is in native's derivation, not in the camera. If it
                // reads 250 mm, the camera really does move between AimAtEye and
                // this line, and the next question is what runs in between -
                // because by now it is not our code.
                try
                {
                    float px = f.origin.x - engineFrame.origin.x;
                    float py = f.origin.y - engineFrame.origin.y;
                    float pz = f.origin.z - engineFrame.origin.z;
                    float pubErr = (float)Math.Sqrt(px * px + py * py + pz * pz);

                    _pubErrSum += pubErr;
                    _pubErrCount++;
                    if (pubErr > _pubErrMax) _pubErrMax = pubErr;

                    if (++_pubLogTick >= 450)
                    {
                        _pubLogTick = 0;
                        float mean = _pubErrCount > 0 ? _pubErrSum / _pubErrCount : 0f;
                        VrLog.Info(string.Format(
                            "Publish D check: eye vs engine camera at the refPos line "
                            + "is {0:F1} mm mean, {1:F1} mm worst, over {2} publish(es) "
                            + "in ~5 s. AimAtEye read back 0.0 mm a few statements "
                            + "earlier, so anything large here appeared in between.",
                            mean * 1000f, _pubErrMax * 1000f, _pubErrCount));
                        _pubErrSum = 0f; _pubErrCount = 0; _pubErrMax = 0f;
                    }
                }
                catch { }

                Vec3 dir = combat.Direction;
                Data[19] = dir.x;
                Data[20] = dir.y;
                Data[21] = dir.z;

                VrCameraDriver.EyeTangents(eye, out float tl, out float tr, out float tu, out float td);
                Data[22] = tl;
                Data[23] = tr;
                Data[24] = tu;
                Data[25] = td;

                if (!IsUsable())
                    return;

                NativeMethods.bvr_set_vp_frame(Data, FloatCount, eye, 1);

                // ...and the eye the engine is NOT drawing, in the same layout.
                //
                // The native side builds a second copy of every view constant
                // buffer from this, so the duplicated scene draws can be issued
                // from the other viewpoint instead of being an exact copy of the
                // first. Its OWN tangents matter: on a canted headset the two
                // eyes get mirrored asymmetric frusta, and reusing this eye's
                // would shear the second image by the difference between them.
                PublishOther(1 - eye, combat);

                if (!_logged)
                {
                    _logged = true;
                    VrLog.Info(string.Format(
                        "VP patch driving: engine camera at ({0:F1},{1:F1},{2:F1}) looking "
                        + "({3:F3},{4:F3},{5:F3}); eye {6} at ({7:F1},{8:F1},{9:F1}); "
                        + "target frustum {10:F1} x {11:F1} deg.",
                        Data[16], Data[17], Data[18], Data[19], Data[20], Data[21],
                        eye, Data[12], Data[13], Data[14],
                        (Math.Atan(tr) - Math.Atan(tl)) * 57.2957795,
                        (Math.Atan(tu) - Math.Atan(td)) * 57.2957795));
                }
            }
            catch (Exception ex)
            {
                // A throw here runs at 90 Hz, so one is a bug and a thousand is a
                // frozen game. Three strikes and the patcher stands down for the
                // run; the native side then times the publication out on its own
                // and stops patching, leaving the engine's plain flat render.
                if (++_failures >= 3)
                {
                    _standDown = true;
                    StandDown();
                    VrLog.Error("VP patch publication failed three times; standing down.", ex);
                }
            }
        }

        /// <summary>
        /// A degenerate frame here would not throw - it would silently produce a
        /// singular delta matrix and blank the world. Cheaper to reject it.
        /// </summary>
        private static bool IsUsable()
        {
            for (int i = 0; i < FloatCount; i++)
            {
                float v = Data[i];
                if (float.IsNaN(v) || float.IsInfinity(v))
                    return false;
            }

            // The eye basis must be a real orientation, and the view direction a
            // real direction. Both are unit vectors when they are healthy.
            if (!IsUnit(Data[0], Data[1], Data[2]) ||
                !IsUnit(Data[4], Data[5], Data[6]) ||
                !IsUnit(Data[8], Data[9], Data[10]) ||
                !IsUnit(Data[19], Data[20], Data[21]))
                return false;

            // A frustum with no width divides by zero on the native side.
            return (Data[23] - Data[22]) > 1e-4f && (Data[24] - Data[25]) > 1e-4f;
        }

        private static bool IsUnit(float x, float y, float z)
        {
            float lengthSquared = x * x + y * y + z * z;
            return lengthSquared > 0.9f && lengthSquared < 1.1f;
        }

        /// <summary>
        /// Tells the native side to stop substituting. Called when the mission
        /// ends, so a stale head pose cannot outlive the scene it belongs to.
        /// The native side also times publications out by itself after 250 ms -
        /// this is the tidy path, that one is the one that survives a crash in
        /// managed code.
        /// </summary>
        public static void StandDown()
        {
            if (!Enabled)
                return;

            try
            {
                NativeMethods.bvr_set_vp_frame(Data, FloatCount, 0, 0);
            }
            catch (Exception ex)
            {
                VrLog.Error("VP patch stand-down failed.", ex);
            }
        }
    }
}
