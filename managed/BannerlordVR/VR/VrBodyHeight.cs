using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 356. The character is the player's height.
    ///
    /// The player's standing eye height is measured by the panel's "Measure my
    /// height" only (TEST 640: no longer on a recentre), as the headset's
    /// height in the stage space - the floor-based space the head and hands are
    /// already mapped from, world scale included. It is kept as a stature
    /// (player_height_m) so the panel's slider reads in the units people know.
    ///
    /// The agent is then scaled so its standing eye - VrHeadBody's upright eye,
    /// in the skeleton's own (model) space - sits at the player's: the engine's
    /// own agent scale (Agent.SetInitialAgentScale, which horses get at spawn),
    /// so the mesh, the weapons, the collision and the engine's eye all grow
    /// together. The engine keeps an agent's scale on its visuals frame, not in
    /// the bones (Mission: GetGlobalFrame()..GetUnitRotFrame(AgentScale)), so the
    /// model-space eye does not move with it and the ratio needs no iterating.
    ///
    /// Under body_anim = head only (that is where the standing eye is known).
    /// </summary>
    internal static class VrBodyHeight
    {
        // ponytail: eye height = 0.936 of stature, the adult average. A
        // per-player ratio only if someone's measured eye sits visibly off.
        private const float EyeRatio = 0.936f;
        private const float MinScale = 0.80f, MaxScale = 1.30f;

        private static readonly MethodInfo SetScale = AccessTools.Method(typeof(Agent), "SetInitialAgentScale");

        /// <summary>The player's standing height, metres; 0 = the game's own size.</summary>
        internal static float PlayerHeight { get; private set; } = Clamp(VrConfig.Float("player_height_m", 0f));

        /// <summary>The scale applied to the player's agent (for the panel).</summary>
        internal static float Scale { get; private set; } = 1f;

        private static Agent _agent;
        private static float _original = 1f, _applied = 1f;
        private static long _checkAt;

        // TEST 356b: set once, not followed. The standing eye breathes about 1 cm
        // with the idle animation, and following it re-scaled the character many
        // times a second (log: 1.087-1.100). It is re-applied only for a new
        // height or a new agent (TEST 640 dropped the 3 cm eye drift).
        private static bool _dirty = true;
        private const float EyeDriftM = 0.03f;

        /// <summary>TEST 384b: the unscaled standing eye a standing human model can have.
        /// Outside it the body is not standing (a rider's saddle height, a fall).</summary>
        private const float MinEyeModel = 1.20f, MaxEyeModel = 2.10f;
        private static float _refusedEye;
        private static bool _broken;

        /// <summary>From the panel's Measure, with that frame's stage-space head:
        /// standing straight, its height IS the player's eye height.</summary>
        internal static void Measure(MatrixFrame headStage, string how)
        {
            Agent agent = Mission.Current?.MainAgent;
            if (agent != null && agent.HasMount)
            {
                VrLog.Info("TEST 356: " + how + " - not measured in the saddle (you may be sitting); "
                           + "measure on foot, standing straight.");
                return;
            }

            float eye = VrCameraDriver.LevelEye(headStage).z;     // TEST 389: as if the head were level
            if (eye < 1.0f || eye > 2.2f)
            {
                VrLog.Info("TEST 356: " + how + " - the headset is " + F(eye) + " m above the floor, not a "
                           + "standing eye height (sitting, or a runtime without a floor-level stage); "
                           + "height not changed. Set it with the VR tab's slider.");
                return;
            }

            SetHeight(eye / EyeRatio, how + ": eye " + F(eye) + " m above the floor");
        }

        /// <summary>The panel's slider, or a measurement. 0 = the game's size.</summary>
        internal static void SetHeight(float height, string how)
        {
            float h = Clamp(height);
            if (Math.Abs(h - PlayerHeight) < 1e-4f)
                return;

            PlayerHeight = h;
            VrConfig.Save("player_height_m", h.ToString("0.000", CultureInfo.InvariantCulture));
            VrLog.Info("TEST 356: your height " + (h > 0f ? F(h) + " m" : "off (the game's own size)")
                       + " (" + how + "), saved.");
            _checkAt = 0;
            _dirty = true;
        }

        /// <summary>
        /// Every on-foot frame of the head-driven body, with the body's standing
        /// eye in model space: scales the agent to the player when they differ.
        /// </summary>
        internal static void Tick(Agent agent, float standingEyeModel)
        {
            if (_broken || agent == null || SetScale == null)
                return;

            try
            {
                if (!ReferenceEquals(agent, _agent))
                {
                    _agent = agent;
                    _original = _applied = agent.AgentScale;
                    _checkAt = 0;
                    _dirty = true;
                }

                // TEST 640: no re-fit to a moved standing eye - a recentre
                // recaptures it (TEST 389), and following it re-sized the body
                // on every recentre. Only a new height or a new agent re-scales.
                if (!_dirty)
                {
                    Check(agent);
                    return;
                }

                // TEST 384b: a standing eye no human model has is a bad reading, not
                // a body to fit: a standing point taken at saddle height (2.65 m)
                // shrank the character to the 0.80 clamp. Keep the last scale.
                if (standingEyeModel < MinEyeModel || standingEyeModel > MaxEyeModel)
                {
                    if (Math.Abs(standingEyeModel - _refusedEye) > EyeDriftM)
                    {
                        _refusedEye = standingEyeModel;
                        VrLog.Warn("TEST 356: not scaling to a standing eye " + F(standingEyeModel) + " m up (unscaled) - "
                                   + "outside " + F(MinEyeModel) + "-" + F(MaxEyeModel) + " m, so the body is not standing.");
                    }
                    return;
                }

                _dirty = false;

                float want = PlayerHeight > 0f && standingEyeModel > 0.5f
                    ? Math.Max(MinScale, Math.Min(MaxScale, PlayerHeight * EyeRatio / standingEyeModel))
                    : _original;

                if (Math.Abs(want - _applied) >= 0.002f)
                {
                    float before = agent.AgentScale;
                    SetScale.Invoke(agent, new object[] { want });
                    _applied = want;
                    Scale = want / Math.Max(1e-3f, _original);
                    _checkAt = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency;
                    VrLog.Info("TEST 356: the character is scaled " + F(before) + " -> " + F(want)
                               + (PlayerHeight > 0f ? " so its eyes (" + F(standingEyeModel) + " m unscaled) sit at yours ("
                                                      + F(PlayerHeight * EyeRatio) + " m)" : " (the game's own size)")
                               + "; the engine reports " + F(agent.AgentScale) + ".");
                    VrMenu.RequestPush();
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 356: scaling the character failed; it keeps the game's size.", ex);
            }
        }

        /// <summary>One look a second after a scale: did the engine draw it that
        /// size? (356: yes - frame 1.095, eye 1.817 m against the player's 1.816.)</summary>
        private static void Check(Agent agent)
        {
            if (_checkAt == 0 || System.Diagnostics.Stopwatch.GetTimestamp() < _checkAt)
                return;

            _checkAt = 0;
            float frame = agent.AgentVisuals?.GetFrame().rotation.s.Length ?? 0f;
            float eyeWorld = VrHeadBody.TryNeutralEyeWorld(agent, out Vec3 eye) ? eye.z - agent.Position.z : 0f;
            VrLog.Info("TEST 356 check: agent scale " + F(agent.AgentScale) + ", visuals frame scale "
                       + F(frame) + "; the standing eye is " + F(eyeWorld) + " m above the feet"
                       + (PlayerHeight > 0f ? " (yours: " + F(PlayerHeight * EyeRatio) + " m)" : "") + ".");
        }

        private static float Clamp(float h) => h <= 0f ? 0f : Math.Max(1.20f, Math.Min(2.30f, h));

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    }
}
