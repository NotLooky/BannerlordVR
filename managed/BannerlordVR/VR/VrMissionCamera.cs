using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Hands the native mission the camera that is actually rendered.
    ///
    /// THE ONE COPY OF THE GAME'S CAMERA NOBODY REPLACED
    ///
    /// MissionScreen.UpdateCamera ends by giving the camera it just built to
    /// three consumers:
    ///
    ///     CombatCamera.Frame = val;
    ///     SceneView.SetCamera(CombatCamera);
    ///     Mission.SetCameraFrame(ref val, 65f / CameraViewAngle, ref val28);
    ///
    /// VrAfrRenderer.AimEngineCamera, in that method's postfix, replaces the
    /// first two with the head pose - AimAtEye, then SetCamera again. The third
    /// was never replaced. The native mission kept the GAME's camera, built from
    /// CameraBearing and CameraElevation, and those follow the head only once
    /// VrEngineAim has calibrated - which waits for the player's own look input
    /// to move the character's look first.
    ///
    /// That is the report exactly: spawn, look behind you without moving, and
    /// nothing is drawn there; turn the character or the horse left or right once
    /// and it is fine for the rest of the battle. The turn is what calibrates the
    /// aim. From then on the game's camera points where the head does - every log
    /// since reads aim-vs-view between 0.0 and 2.4 deg - so the mission's copy has
    /// been right in steady play and wrong for exactly the seconds before the
    /// first turn.
    ///
    /// WHAT IS INFERRED
    ///
    /// Which native system reads the mission's camera to decide what is drawn is
    /// not reverse engineered: the binding is by ID, and the only named string
    /// near it is the FMOD listener's. The case rests on elimination - with the
    /// other two replaced, this is the only input from the game's camera that
    /// differs between "before the first turn" and "after". The instrument below
    /// says in one run whether that difference was really there.
    ///
    /// WHAT ELSE IT MOVES
    ///
    /// SetCameraFrame also places the sound listener, which now turns with the
    /// head from the first frame instead of only after calibration - the right
    /// answer in a headset anyway. The zoom factor and the attenuation position
    /// are the game's own formula, recomputed here, so nothing else changes.
    /// </summary>
    public static class VrMissionCamera
    {
        /// <summary>0 leaves the mission with the game's own camera, which is what
        /// every build before TEST 179 did.</summary>
        private static readonly bool Enabled = VrConfig.Bool("mission_camera_head", true);

        private static bool _loggedFirst;
        private static bool _broken;

        private static int _writes;
        private static int _samples;
        private static int _notDriving;
        private static float _divSum;
        private static float _divMax;
        private static float _divMaxNotDriving;

        /// <summary>
        /// Call from UpdateCamera's postfix AFTER CombatCamera has been aimed at the
        /// eye, so the frame read back is the one the scene renders from.
        /// </summary>
        public static void FollowHead(MissionScreen screen, Camera combat)
        {
            if (!Enabled || _broken || screen == null || combat == null || !VrCameraDriver.HaveEyeFrames)
                return;

            try
            {
                Mission mission = Mission.Current;
                if (mission == null)
                    return;

                // What the game just told the mission, before it is replaced. Read
                // for the instrument only.
                MatrixFrame game = mission.GetCameraFrame();

                // The camera AimAtEye just placed, in the engine's own camera
                // convention, so no basis is rebuilt here and there is no convention
                // to get wrong. The origin moves to the midpoint of the eyes: under
                // AFR the camera alternates between them, and the listener has no
                // business hopping 65 mm a frame.
                MatrixFrame frame = combat.Frame;
                frame.origin = VrCameraDriver.HeadFrame.origin;

                // The game's own attenuation position: the followed agent's eyes,
                // blended toward the camera by the mission's factor. Same formula as
                // UpdateCamera, with the rendered camera in place of its own.
                Agent followed = screen.LastFollowedAgent;
                Vec3 attenuation = followed != null ? followed.GetEyeGlobalPosition() : frame.origin;
                float blend = mission.ListenerAndAttenuationPosBlendFactor;
                if (followed != null && blend > 0f)
                    attenuation += (frame.origin - attenuation) * blend;

                float viewAngle = screen.CameraViewAngle;
                float zoom = viewAngle > 1e-3f ? 65f / viewAngle : 1f;

                mission.SetCameraFrame(ref frame, zoom, ref attenuation);

                Measure(game, frame);

                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    VrLog.Info("Mission camera: the native mission is now handed the RENDERED "
                               + "camera every frame instead of the game's own, so it follows the "
                               + "head from the first frame rather than only after the engine aim "
                               + "has calibrated. Set mission_camera_head = 0 to go back.");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Mission camera: handing the mission the rendered camera failed; it "
                            + "keeps the game's own camera for the rest of the session.", ex);
            }
        }

        /// <summary>
        /// Every ~5 s: how far the game's camera pointed from the rendered one, and
        /// how far while the engine aim was not yet driving the game's look. Large
        /// there and near zero after is the gap this class closes; near zero
        /// throughout means the mission never had a wrong camera and the missing
        /// world has to be looked for elsewhere.
        /// </summary>
        private static void Measure(MatrixFrame game, MatrixFrame ours)
        {
            // Both are camera frames, which look down -u (the convention line at
            // startup measured it), so the angle between the u axes is the angle
            // between the view directions.
            Vec3 a = game.rotation.u;
            Vec3 b = ours.rotation.u;
            float la = a.Length;
            float lb = b.Length;
            if (la > 1e-4f && lb > 1e-4f)
            {
                float c = (a.x * b.x + a.y * b.y + a.z * b.z) / (la * lb);
                if (c > 1f) c = 1f;
                if (c < -1f) c = -1f;
                float deg = (float)Math.Acos(c) * 57.2957795f;

                _samples++;
                _divSum += deg;
                if (deg > _divMax) _divMax = deg;

                if (!VrEngineAim.MaintainsBodyHeading)
                {
                    _notDriving++;
                    if (deg > _divMaxNotDriving) _divMaxNotDriving = deg;
                }
            }

            if (++_writes < 450)
                return;

            VrLog.Info(string.Format(
                "Mission camera: {0} frame(s) handed the rendered camera in ~5 s. The game's own "
                + "camera pointed {1:F1} deg from it on average, {2:F1} deg at worst; {3} of those "
                + "frames were while the engine aim was not yet driving the game's look (worst "
                + "{4:F1} deg). Large there and near zero after is the gap this closes.",
                _writes, _samples > 0 ? _divSum / _samples : 0f, _divMax, _notDriving,
                _divMaxNotDriving));

            _writes = 0;
            _samples = 0;
            _notDriving = 0;
            _divSum = 0f;
            _divMax = 0f;
            _divMaxNotDriving = 0f;
        }
    }
}
