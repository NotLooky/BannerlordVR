using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Rides the GAME'S OWN first-person camera instead of synthesising one.
    ///
    /// WHAT THIS REPLACES
    ///
    /// The mod used to build its own first person: VrEyePoint computed an eye
    /// point from the agent, the anchor was that point plus a body heading, and
    /// the head pose went on top. The game meanwhile stayed in THIRD person,
    /// because nothing here ever set Mission.CameraIsFirstPerson. So there were
    /// two first-person answers in the process at once - the game's, unused, and
    /// ours, rendered - and the engine's camera, which decides culling, aiming
    /// and the monitor, was the third-person one that agreed with neither.
    ///
    /// WHY THE GAME'S IS THE BETTER ANCHOR
    ///
    /// It is not a guess. MissionScreen.UpdateCamera places the first-person
    /// camera off the skeleton:
    ///
    ///     ForceUpdateBoneFrames();
    ///     m = GetBoneEntitialFrame(Monster.HeadLookDirectionBoneIndex, true);
    ///     m.origin = m.TransformToParent(Monster.FirstPersonCameraOffsetWrtHead);
    ///     origin = AgentVisuals.GetFrame().TransformToParentDouble(m.origin);
    ///
    /// which is mount-correct for the same reason VrEyePoint's own header gives -
    /// the rider's skeleton is placed in the saddle by the engine - and is
    /// additionally collision-aware, animation-correct and smoothed by code
    /// TaleWorlds tuned. VrEyePoint's best effort was Agent.Position plus a
    /// computed eye height, which is a formula standing in for exactly this.
    ///
    /// WHERE IT IS TAKEN, AND WHY THAT SEAM
    ///
    /// UpdateCamera ends with "CombatCamera.Frame = cameraFrame", so its postfix
    /// is the one instant CombatCamera holds the game's answer and nothing else.
    /// A frame later is too late: VrAfrRenderer.AimEngineCamera writes the VR pose
    /// into that same camera, and reading it back then returns our own output -
    /// the feedback loop ResolveAnchor has always had to defend against. Capture
    /// therefore runs at the TOP of that postfix, ahead of every VR write.
    ///
    /// This works only because AFR's default camera mode is Engine, which moves
    /// CombatCamera in place and never sets CustomCamera. With CustomCamera set,
    /// CheckForUpdateCamera returns early and UpdateCamera never runs at all -
    /// there would be no game camera to ride. Capture simply finds nothing in
    /// that case and the anchor falls back, which is the honest behaviour.
    ///
    /// ROTATION IS DELIBERATELY NOT TAKEN
    ///
    /// Only the origin. The anchor's rotation stays on VrEngineAim.BodyHeading(),
    /// which is integrated from the mouse alone. The game's camera rotation
    /// already contains the head yaw this mod injected into it, so anchoring to
    /// it and then composing the head pose on top would apply the head twice and
    /// turn the world twice as far as the head - the failure VrEngineAim's header
    /// records happening twice already. Position has no such problem: nothing
    /// here writes the camera's position, so reading it back cannot echo.
    /// </summary>
    public static class VrGameCamera
    {
        /// <summary>Whether to force the game into its own first person and ride
        /// that camera. 0 restores the synthesised eye point in VrEyePoint.</summary>
        public static readonly bool Enabled = VrConfig.Bool("game_first_person", true);

        /// <summary>
        /// How far the captured camera may sit from the agent before it is
        /// rejected, metres.
        ///
        /// A sanity check on WHICH branch of UpdateCamera actually ran.
        /// CameraIsFirstPerson being true is necessary but not sufficient - the
        /// first-person placement also needs an agent to follow, that agent to be
        /// the main agent, valid visuals and photo mode off, and when any of that
        /// fails the game builds a third-person or spectator camera into the very
        /// same field. Those sit several metres back; a first-person camera is at
        /// most a rider's height above the horse's ground position. Rejecting on
        /// distance tells the two apart without duplicating the engine's
        /// conditions here, where they would rot out of step with it.
        /// </summary>
        private static readonly float MaxDistance = VrConfig.Float("game_first_person_max_m", 3.5f);

        private static bool _have;
        private static Vec3 _origin;

        private static bool _haveRaw;
        private static MatrixFrame _rawFrame;

        private static bool _loggedForce;
        private static bool _loggedCapture;
        private static bool _loggedReject;
        private static bool _loggedNoCapture;
        private static bool _broken;

        /// <summary>True while the anchor is genuinely riding the game's camera.
        /// Read by VrEyePoint so it can say it stood down rather than looking as
        /// though it silently stopped mattering.</summary>
        public static bool Driving
        {
            get { return Enabled && _have; }
        }

        /// <summary>
        /// Puts the game into first person, from UpdateCamera's PREFIX so the
        /// camera it is about to build is the first-person one.
        ///
        /// Written every frame rather than once: the player's own toggle key
        /// flips this field in HandleInputs, and in VR there is nothing for a
        /// third-person camera to be - the headset would show the world from
        /// behind a body whose head is where the player's head is not.
        /// </summary>
        public static void ForceFirstPerson()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                Mission mission = Mission.Current;

                // No main agent means spectating or dead, and the game's own
                // first person has nothing to attach to. Leave the camera alone
                // rather than forcing a mode it cannot honour.
                if (mission == null || mission.MainAgent == null)
                    return;

                if (mission.CameraIsFirstPerson)
                    return;

                mission.CameraIsFirstPerson = true;

                if (!_loggedForce)
                {
                    _loggedForce = true;
                    VrLog.Info("The game is now held in its OWN first person; the mod no "
                               + "longer synthesises one. Set game_first_person = 0 to go "
                               + "back to the computed eye point.");
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("Could not hold the game in first person; the anchor falls back "
                            + "to the synthesised eye point for the rest of the session.", ex);
            }
        }

        /// <summary>
        /// Takes the camera the game just built. Call at the TOP of UpdateCamera's
        /// postfix, before any VR write reaches CombatCamera.
        /// </summary>
        public static void Capture(MissionScreen screen)
        {
            if (_broken)
                return;

            // Cleared every attempt, so a frame the game did not build a usable
            // first-person camera for falls back rather than reusing a stale one.
            _have = false;
            _haveRaw = false;

            try
            {
                Camera combat = screen == null ? null : screen.CombatCamera;
                if (combat == null)
                    return;

                MatrixFrame frame = combat.Frame;
                if (!IsUsable(frame.origin))
                    return;

                // THE WHOLE GAME CAMERA, captured unconditionally and BEFORE any
                // VR write lands on it.
                //
                // Deliberately not gated on game_first_person, because this half
                // is not a feature - it is what makes reading the engine camera
                // safe at all. In AFR's Engine mode we write our own eye pose
                // into CombatCamera every frame, so anything that reads it back
                // later in the frame gets our output rather than the game's, and
                // an anchor built from that adds the head offset to itself once
                // per frame. Captured here, it is still the engine's own answer.
                _rawFrame = frame;
                _haveRaw = true;

                if (!Enabled)
                    return;

                Mission mission = Mission.Current;
                if (mission == null || !mission.CameraIsFirstPerson)
                    return;

                Agent agent = mission.MainAgent;
                if (agent == null)
                    return;

                Vec3 origin = frame.origin;

                // See MaxDistance: this is what says the first-person branch is
                // the one that ran.
                float distance = (origin - agent.Position).Length;
                if (distance > MaxDistance)
                {
                    if (!_loggedReject)
                    {
                        _loggedReject = true;
                        VrLog.Info(string.Format(
                            "Game camera {0:F2} m from the agent, past the {1:F2} m first-person "
                            + "limit, so the engine built a third-person or spectator camera this "
                            + "frame. The anchor falls back to the computed eye point while that "
                            + "lasts. Raise game_first_person_max_m if this is wrong.",
                            distance, MaxDistance));
                    }

                    return;
                }

                _origin = origin;
                _have = true;

                if (!_loggedCapture)
                {
                    _loggedCapture = true;
                    VrLog.Info(string.Format(
                        "Anchored to the game's own first-person camera, {0:F2} m from the "
                        + "agent's position. Rotation still comes from the mouse-integrated "
                        + "body heading, so the head is never applied twice.", distance));
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                _have = false;
                VrLog.Error("Reading the game's first-person camera failed; the anchor falls "
                            + "back to the synthesised eye point for the rest of the session.", ex);
            }
        }

        /// <summary>The origin the game's own first-person camera was built at
        /// this frame, if there was one.</summary>
        public static bool TryGetEyeOrigin(out Vec3 origin)
        {
            origin = _origin;

            if (Enabled && !_have && !_loggedNoCapture)
            {
                _loggedNoCapture = true;
                VrLog.Info("No game first-person camera to anchor to yet; using the computed "
                           + "eye point until there is one. Expected for the first frames of a "
                           + "mission, and whenever there is no main agent.");
            }

            return Enabled && _have;
        }

        /// <summary>
        /// The engine's own camera for this frame, whatever kind it is - the
        /// deployment camera before the player has spawned, a spectator camera
        /// after they die, or the first-person one in between.
        ///
        /// This is what the anchor's no-agent fallback must read INSTEAD of
        /// CombatCamera. Reading CombatCamera there was the "camera drifts
        /// backward before pressing Ready" bug: with no main agent the anchor
        /// falls back to the engine camera, but by that point in the frame the
        /// engine camera is our own last eye pose, so the anchor grew by the head
        /// offset every frame it was read.
        ///
        /// The echo guard was supposed to catch exactly that and could not. It
        /// compares against the last EYE-0 origin, while AFR alternates which eye
        /// it writes - so on eye-1 frames the two differ by the interpupillary
        /// distance, about 64 mm, against a guard that treats anything over 10 mm
        /// as genuine. Half the frames therefore read as real camera movement.
        /// That guard is still there and still worth having, but it is now a
        /// backstop rather than the mechanism.
        /// </summary>
        public static bool TryGetCameraFrame(out MatrixFrame frame)
        {
            frame = _rawFrame;
            return _haveRaw;
        }

        /// <summary>Forgets the captured camera. Called when the mission changes,
        /// so a new scene cannot be anchored to the old one's camera.</summary>
        public static void Reset()
        {
            _have = false;
            _haveRaw = false;
        }

        private static bool IsUsable(Vec3 v)
        {
            return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
                && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
        }
    }
}
