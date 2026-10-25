using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery spawns PenetrationDeep / skewer / bleed effects on the
    // part; Bannerlord ships matching blood particle systems in Native
    // (particle_systems2.xml). Bursts are rate limited per kind.
    internal static class BloodFx
    {
        public enum Kind { Enter, Inside, Exit, Drip, Flow }
        private static readonly string[] Names =
            { "psys_game_blood_sword_enter", "psys_game_blood_sword_inside", "psys_game_blood_sword_exit", "psys_game_blood_1", "psys_game_blood_1" };
        private static readonly float[] MinInterval = { .05f, .15f, .1f, .25f, .05f };
        private static readonly int[] Ids = { int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        private static readonly float[] Last = { -1, -1, -1, -1, -1 };

        public static void Burst(Kind kind, Vec3 position, Vec3 direction)
        {
            Mission mission = Mission.Current;
            if (mission?.Scene == null || !InteractionMath.IsFinite(position)) return;
            int k = (int)kind;
            float now = mission.CurrentTime;
            if (Last[k] >= 0 && now >= Last[k] && now - Last[k] < MinInterval[k]) return;
            Last[k] = now;
            try
            {
                if (Ids[k] == int.MinValue) Ids[k] = ParticleSystemManager.GetRuntimeIdByName(Names[k]);
                if (Ids[k] < 0) return;
                MatrixFrame frame = MatrixFrame.Identity;
                frame.origin = position;
                if (InteractionMath.IsFinite(direction) && direction.LengthSquared > 1e-6f)
                {
                    frame.rotation.f = direction.NormalizedCopy();
                    frame.rotation.OrthonormalizeAccordingToForwardAndKeepUpAsZAxis();
                }
                mission.Scene.CreateBurstParticle(Ids[k], frame);
            }
            catch (Exception ex)
            {
                Ids[k] = -1;
                PhysicsLog.Error("Blood particle " + Names[k] + " unavailable.", ex);
            }
        }
    }
}
