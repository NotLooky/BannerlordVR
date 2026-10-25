using System;

namespace BannerlordVR.VR.Physics
{
    public enum PushMaterial { Punch, Kick, Blunt, BluntLight, Slash, SlashHeavy, SlashAxe, Pierce, Ragdoll }
    public enum PushBodyZone { Torso, Hand, HeadOrLeg }
    // Brain.Stagger context of Blade & Sorcery BrainModuleHitReaction.
    public enum PushContext { Idle, Combat, Attack, AttackJump, Stagger, StaggerFull }
    public enum PushEffect { None, StaggerLight, StaggerMedium, StaggerFull, Destabilize }

    // Blade & Sorcery push model. Damager.GetPushLevel returns the highest
    // level whose impact speed threshold is exceeded (0 when none is), taken
    // from the DamageModifier catalog. BrainModuleHitReaction then picks an
    // effect from the creature's push behaviours; values below are those of
    // the human brains (Brain_Human*.json, pushHitBehaviors) and of the
    // DamageModifier_*.json hitVelocity / throwVelocity tables.
    public static class PushReaction
    {
        private static readonly float[] Punch = { 2, 4, 6, 12, 24 };
        private static readonly float[] Kick = { 1, 1.5f, 2, 3, 18 };
        private static readonly float[] Blunt = { 4, 7.5f, 9, 12, 24 };
        private static readonly float[] BluntLight = { 7, 9, 12, 24, 28 };
        private static readonly float[] Slash = { 3, 8 };
        private static readonly float[] SlashHeavy = { 3, 6, 9, 20, 24 };
        private static readonly float[] SlashAxe = { 3, 6, 9, 14, 24 };
        private static readonly float[] Pierce = { 6, 15 };
        private static readonly float[] PierceThrown = { 3, 6 };
        private static readonly float[] Ragdoll = { 3, 4.5f, 6, 12, 24 };

        public static float[] Thresholds(PushMaterial material, bool thrown = false)
        {
            switch (material)
            {
                case PushMaterial.Punch: return Punch;
                case PushMaterial.Kick: return Kick;
                case PushMaterial.Blunt: return Blunt;
                case PushMaterial.BluntLight: return BluntLight;
                case PushMaterial.Slash: return Slash;
                case PushMaterial.SlashHeavy: return SlashHeavy;
                case PushMaterial.SlashAxe: return SlashAxe;
                case PushMaterial.Pierce: return thrown ? PierceThrown : Pierce;
                default: return Ragdoll;
            }
        }

        public static int Level(PushMaterial material, float impactSpeed, bool thrown = false)
        {
            // Punches and kicks have infinite throw thresholds in the catalog.
            if (thrown && (material == PushMaterial.Punch || material == PushMaterial.Kick)) return 0;
            if (!InteractionMath.IsFinite(impactSpeed) || impactSpeed <= 0) return 0;
            float[] levels = Thresholds(material, thrown);
            for (int i = levels.Length - 1; i >= 0; i--) if (impactSpeed > levels[i]) return i;
            return 0;
        }

        // Brain_Human pushHitBehaviors; body-part specific rows replace the
        // generic row of the same level (their merge keys share the level).
        public static PushEffect HitEffect(int level, PushBodyZone zone, PushContext context)
        {
            if (level < 0) return PushEffect.None;
            level = Math.Min(level, 3);
            PushEffect[] row = Row(level, zone);
            return row[(int)context];
        }

        // Grab and throw use pushGrabThrowBehaviors (generic rows only).
        public static PushEffect GrabEffect(int level, PushContext context)
        {
            if (level < 0) return PushEffect.None;
            PushEffect[] row = level == 0 ? GrabZero : GrabOne;
            return row[(int)context];
        }

        // Columns follow PushContext: idle, combat, attack, attack jump,
        // during stagger, during full stagger.
        private static readonly PushEffect N = PushEffect.None, L = PushEffect.StaggerLight,
            M = PushEffect.StaggerMedium, F = PushEffect.StaggerFull, D = PushEffect.Destabilize;
        private static readonly PushEffect[] Zero = { L, N, N, N, N, N };
        private static readonly PushEffect[] ZeroHand = { L, N, L, N, N, N };
        private static readonly PushEffect[] One = { M, L, M, N, M, M };
        private static readonly PushEffect[] OneHeadLeg = { F, M, F, D, F, F };
        private static readonly PushEffect[] Two = { D, L, M, D, M, F };
        private static readonly PushEffect[] TwoHeadLeg = { D, F, F, D, D, D };
        private static readonly PushEffect[] Three = { D, M, F, D, F, D };
        private static readonly PushEffect[] ThreeHeadLeg = { D, F, D, D, D, D };
        private static readonly PushEffect[] GrabZero = { F, M, M, D, F, F };
        private static readonly PushEffect[] GrabOne = { D, F, F, D, D, D };

        private static PushEffect[] Row(int level, PushBodyZone zone)
        {
            switch (level)
            {
                case 0: return zone == PushBodyZone.Hand ? ZeroHand : Zero;
                case 1: return zone == PushBodyZone.HeadOrLeg ? OneHeadLeg : One;
                case 2: return zone == PushBodyZone.HeadOrLeg ? TwoHeadLeg : Two;
                default: return zone == PushBodyZone.HeadOrLeg ? ThreeHeadLeg : Three;
            }
        }

        // BrainModuleHitReaction.staggerLight / staggerMedium recoil amounts;
        // a full stagger is 1 and a destabilization 2.
        public static float Recoil(PushEffect effect)
        {
            switch (effect)
            {
                case PushEffect.StaggerLight: return .2f;
                case PushEffect.StaggerMedium: return .6f;
                case PushEffect.StaggerFull: return 1;
                case PushEffect.Destabilize: return 2;
                default: return 0;
            }
        }
    }
}
