using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Measures the bone-writing seam, before any arm solver is built on it.
    ///
    /// WHY THIS EXISTS
    ///
    /// Two notes in this repo disagree about whether our bone writes survive.
    /// VrMotionHands says the hand posing writes bone frames every frame that the
    /// animation writes back over. MissionTickPatch says moving to AfterMissionTick
    /// is precisely what fixed that. Both cannot be current, and an arm solve is a
    /// lot of work to build on top of an unanswered question.
    ///
    /// WHAT A NEXT-FRAME READ-BACK CANNOT TELL YOU
    ///
    /// The obvious probe - write a pose, read it back next frame, report the
    /// difference - does not answer anything here. The animation is SUPPOSED to
    /// repose every bone every frame; that is its job. A large difference next
    /// frame is the system working correctly, not a failure. So that number would
    /// have been measured, believed, and meant nothing.
    ///
    /// THE TWO QUESTIONS THAT ARE ACTUALLY LOAD-BEARING
    ///
    /// 1. DID THE WRITE LAND? Same frame, immediately after
    ///    UpdateEntitialFramesFromLocalFrames, does the bone entitial frame equal
    ///    what we asked for? This catches the realistic failure - a parent-space
    ///    error, or scale leaking through a bone frame - rather than a race.
    ///    Nothing in managed code runs between this point and the GPU skinning, so
    ///    if the skeleton holds the value we asked for, the renderer gets it. That
    ///    is also the seam the weapon already proves: it tracks correctly today,
    ///    and the hand bone write is what used to carry it.
    ///
    /// 2. IS THE BONE REPOSED BETWEEN FRAMES? At the top of the next frame,
    ///    BEFORE we write anything, does the bone still hold our value? If it
    ///    does, our writes ACCUMULATE, and anything written incrementally will
    ///    wind up over time - which is exactly the trap waiting for the forearm
    ///    twist helpers. If it does not, each frame starts clean.
    ///
    /// Both are measured in ENTITIAL space, which is relative to the agent entity,
    /// so the agent walking or turning between frames does not enter the numbers.
    ///
    /// This writes nothing of its own. It observes the writes VrWeaponHands is
    /// already making, so it cannot disturb what it is measuring.
    /// </summary>
    public static class VrArmSeam
    {
        public static bool Enabled { get; } = VrConfig.Bool("arm_seam_probe", true);

        /// <summary>Writes to gather before reporting. Long enough that a still
        /// moment cannot pass for a stuck bone.</summary>
        private static readonly int Window = VrConfig.Int("arm_seam_probe_frames", 360);

        /// <summary>Writes to throw away first.
        ///
        /// The anchor is not valid on the first frames of a mission - one measured
        /// run had the hand target 136 METRES out before it settled - and a single
        /// sample that size buries a millimetre-scale average underneath it. The
        /// number being measured is a steady-state one, so the transient is not
        /// part of it.</summary>
        private static readonly int Settle = VrConfig.Int("arm_seam_probe_settle", 120);

        private static int _seen;

        private static bool _broken;
        private static bool _reported;

        // What we last wrote, per bone index, in entitial space.
        private const int MaxBones = 64;
        private static readonly MatrixFrame[] _wrote = new MatrixFrame[MaxBones];
        private static readonly bool[] _haveWrote = new bool[MaxBones];

        // Question 1: same-frame landing error.
        private static float _landPosSum, _landPosMax;
        private static float _landRotSum, _landRotMax;
        private static int _landCount;

        // Question 2: how far the animation moved the bone before we saw it again.
        private static float _resetPosSum, _resetPosMax;
        private static float _resetRotSum, _resetRotMax;
        private static int _resetCount;
        private static int _resetZero;

        /// <summary>
        /// Called at the top of the tick, BEFORE any write this frame. Answers
        /// question 2 for every bone we wrote last frame.
        /// </summary>
        public static void BeforeWrites(Skeleton skeleton)
        {
            if (!Enabled || _broken || _reported || skeleton == null)
                return;

            try
            {
                for (sbyte i = 0; i < MaxBones && i >= 0; i++)
                {
                    if (!_haveWrote[i])
                        continue;

                    MatrixFrame now = skeleton.GetBoneEntitialFrameWithIndex(i);

                    float dPos, dRot;
                    Difference(_wrote[i], now, out dPos, out dRot);

                    _resetPosSum += dPos;
                    _resetRotSum += dRot;
                    if (dPos > _resetPosMax) _resetPosMax = dPos;
                    if (dRot > _resetRotMax) _resetRotMax = dRot;

                    // Exactly unchanged, to float precision. Counted separately
                    // because "the mean is small" and "it never moves at all" are
                    // different findings, and only the second one forces absolute
                    // writes on the twist helpers.
                    if (dPos < 1e-6f && dRot < 1e-4f)
                        _resetZero++;

                    _resetCount++;
                    _haveWrote[i] = false;
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("The arm seam probe failed. It measures only; nothing "
                            + "else is affected.", ex);
            }
        }

        /// <summary>
        /// Called once per frame AFTER UpdateEntitialFramesFromLocalFrames, with
        /// the entitial frame this bone was meant to end up at. Answers question 1,
        /// and records the value for question 2 next frame.
        /// </summary>
        public static void AfterWrite(Skeleton skeleton, sbyte bone, MatrixFrame wanted)
        {
            if (!Enabled || _broken || _reported || skeleton == null)
                return;
            if (bone < 0 || bone >= MaxBones)
                return;

            if (++_seen <= Settle)
                return;

            try
            {
                MatrixFrame landed = skeleton.GetBoneEntitialFrameWithIndex(bone);

                float dPos, dRot;
                Difference(wanted, landed, out dPos, out dRot);

                _landPosSum += dPos;
                _landRotSum += dRot;
                if (dPos > _landPosMax) _landPosMax = dPos;
                if (dRot > _landRotMax) _landRotMax = dRot;
                _landCount++;

                _wrote[bone] = landed;
                _haveWrote[bone] = true;

                if (_landCount >= Window)
                    Report();
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("The arm seam probe failed. It measures only; nothing "
                            + "else is affected.", ex);
            }
        }

        /// <summary>Positional difference in millimetres, angular in degrees. The
        /// rotation is compared on its forward axis, which is the one a wrong
        /// answer shows up in first and does not need a quaternion to read.</summary>
        private static void Difference(MatrixFrame a, MatrixFrame b,
                                       out float posMm, out float rotDeg)
        {
            posMm = (a.origin - b.origin).Length * 1000f;

            Vec3 fa = a.rotation.f;
            Vec3 fb = b.rotation.f;
            fa.Normalize();
            fb.Normalize();

            float dot = fa.x * fb.x + fa.y * fb.y + fa.z * fb.z;
            if (dot > 1f) dot = 1f;
            if (dot < -1f) dot = -1f;

            rotDeg = (float)Math.Acos(dot) * 57.2957795f;
        }

        private static void Report()
        {
            _reported = true;

            float landPos = _landCount > 0 ? _landPosSum / _landCount : 0f;
            float landRot = _landCount > 0 ? _landRotSum / _landCount : 0f;

            VrLog.Info(string.Format(
                "Arm seam, question 1 - did the write land? Over {0} writes the bone "
                + "ended the frame {1:F3} mm and {2:F3} deg from where it was asked to "
                + "be, on average; worst {3:F3} mm / {4:F3} deg. Near zero means the "
                + "skeleton holds what we give it and the arm solve can be built on "
                + "this seam. Large means the parent-space walk or a scaled bone frame "
                + "is wrong - not a race, and not fixable by moving the seam.",
                _landCount, landPos, landRot, _landPosMax, _landRotMax));

            if (_resetCount == 0)
            {
                VrLog.Info("Arm seam, question 2 - no samples. Nothing was written on "
                           + "two consecutive frames, so whether the animation reposes "
                           + "our bones is still unmeasured.");
                return;
            }

            float resetPos = _resetPosSum / _resetCount;
            float resetRot = _resetRotSum / _resetCount;
            int zeroPct = _resetZero * 100 / _resetCount;

            VrLog.Info(string.Format(
                "Arm seam, question 2 - is the bone reposed between frames? Over {0} "
                + "frames the animation had moved it {1:F2} mm and {2:F2} deg from our "
                + "value by the time we looked again; worst {3:F2} mm / {4:F2} deg, and "
                + "it was unchanged on {5} percent of frames. A healthy number here is "
                + "NOT zero - the animation is supposed to repose every bone, and that "
                + "is what makes each frame start clean. Near 100 percent unchanged "
                + "means our writes accumulate, and anything written incrementally - "
                + "the forearm twist helpers especially - will wind up over minutes and "
                + "must be written absolute against the rest pose.",
                _resetCount, resetPos, resetRot, _resetPosMax, _resetRotMax, zeroPct));
        }

        public static void Reset()
        {
            for (int i = 0; i < MaxBones; i++)
                _haveWrote[i] = false;

            _landPosSum = _landPosMax = _landRotSum = _landRotMax = 0f;
            _landCount = 0;
            _resetPosSum = _resetPosMax = _resetRotSum = _resetRotMax = 0f;
            _resetCount = 0;
            _resetZero = 0;
            _reported = false;
            _seen = 0;
        }
    }
}
