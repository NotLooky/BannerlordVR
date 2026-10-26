using System;

namespace BannerlordVR.VR.Physics
{
    public enum PenetrationModifier { Pierce, PierceFine, PierceWarpick, PierceArrow, Slash, SlashAxe }

    // Blade & Sorcery DamageModifier_*.json, generated from the game catalog:
    // per target material, the first modifier row that matches the weapon
    // tier (DamageModifierData.GetModifier) for an item without imbue. The
    // rows filtered on a Fire imbue are skipped.
    public static class PenetrationModifiers
    {
        public struct Row
        {
            public bool Valid, Allow, Pressure;
            public float MinVelocity, DamperMultiplier, PressureForceMultiplier;
            // Modifier.minVelocity: slower contacts deal no damage at all.
            public float DamageMinVelocity;
            // penetrationVelocityMaxDepth: the joint limit is the curve value
            // at (impact speed - min speed); every catalog curve rises
            // linearly from 0 at 0 m/s to this depth at 1 m/s, then holds.
            public float VelocityDepth;
            public float DepthLimit(float speed) =>
                VelocityDepth <= 0 ? float.PositiveInfinity : VelocityDepth * Math.Max(0, Math.Min(1, speed - MinVelocity));
        }
        private static Row R(bool allow, float damageMin, float min, float damper, bool pressure, float pressureForce, float depth) =>
            new Row { Valid = true, Allow = allow, DamageMinVelocity = damageMin, MinVelocity = min, DamperMultiplier = damper, Pressure = pressure,
                PressureForceMultiplier = pressureForce, VelocityDepth = depth };

        private static readonly Row[][][] Table =
        {
            // DamageModifier_Pierce
            new[] {
                // Flesh
                new[] { R(true, 1f, 1f, 3f, true, 2f, 0f), R(true, 1f, 0.5f, 1.5f, true, 2f, 0f), R(true, 1f, 1f, 1f, true, 2f, 0f), R(true, 1f, 1f, 0.75f, true, 2f, 0f), R(true, 1f, 1f, 0.5f, true, 2f, 0f), R(true, 1f, 1f, 0.5f, true, 2f, 0f), R(true, 1f, 1f, 0.5f, true, 2f, 0f) },
                // Leather
                new[] { R(true, 3f, 6f, 6f, false, 1f, 0.1f), R(true, 3f, 3f, 2.5f, true, 1f, 0f), R(true, 3f, 3f, 2.5f, true, 1f, 0f), R(true, 3f, 3f, 1.25f, true, 1f, 0f), R(true, 3f, 3f, 1.25f, true, 1f, 0f), R(true, 3f, 3f, 1.25f, true, 1f, 0f), R(true, 3f, 3f, 1.25f, true, 1f, 0f) },
                // Chainmail
                new[] { R(false, 3f, 9f, 3f, false, 1f, 0.5f), R(true, 3f, 3f, 6f, false, 1f, 0.2f), R(true, 3f, 3f, 6f, false, 1f, 0.2f), R(true, 3f, 3f, 4f, false, 1f, 0.2f), R(true, 3f, 3f, 4f, false, 1f, 0.2f), R(true, 3f, 3f, 4f, false, 1f, 0.2f), R(true, 3f, 3f, 4f, false, 1f, 0.2f) },
                // Plate
                new[] { R(true, 12f, 50f, 12f, false, 1f, 0f), R(true, 12f, 50f, 12f, false, 1f, 0f), R(true, 12f, 50f, 12f, false, 1f, 0f), R(true, 12f, 50f, 12f, false, 1f, 0f), R(true, 12f, 50f, 12f, false, 1f, 0f), R(true, 12f, 50f, 12f, false, 1f, 0f), R(true, 12f, 50f, 12f, false, 1f, 0f) }
            },
            // DamageModifier_PierceFine
            new[] {
                // Flesh
                new[] { R(true, 1f, 1f, 3f, true, 1f, 0f), R(true, 1f, 1f, 2f, true, 1f, 0f), R(true, 1f, 1f, 1.5f, true, 1f, 0f), R(true, 1f, 1f, 1f, true, 1f, 0f), R(true, 1f, 1f, 0.75f, true, 1f, 0f), R(true, 1f, 1f, 0.75f, true, 1f, 0f), R(true, 1f, 1f, 0.75f, true, 1f, 0f) },
                // Leather
                new[] { R(true, 2f, 4f, 6f, false, 1f, 0.1f), R(true, 2f, 2f, 3f, true, 1f, 0f), R(true, 2f, 2f, 3f, true, 1f, 0f), R(true, 2f, 2f, 1.25f, true, 1f, 0f), R(true, 2f, 2f, 1.25f, true, 1f, 0f), R(true, 2f, 2f, 1.25f, true, 1f, 0f), R(true, 2f, 2f, 1.25f, true, 1f, 0f) },
                // Chainmail
                new[] { R(false, 3f, 9f, 3f, false, 1f, 0.5f), R(true, 3f, 3f, 3f, false, 1f, 0.5f), R(true, 3f, 3f, 3f, false, 1f, 0.5f), R(true, 3f, 3f, 2f, true, 1f, 0.5f), R(true, 3f, 3f, 2f, true, 1f, 0.5f), R(true, 3f, 3f, 2f, true, 1f, 0.5f), R(true, 3f, 3f, 2f, true, 1f, 0.5f) },
                // Plate
                new[] { R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f) }
            },
            // DamageModifier_PierceWarpick
            new[] {
                // Flesh
                new[] { R(true, 3f, 3f, 2f, false, 1f, 0f), R(true, 3f, 3f, 1f, true, 1f, 0f), R(true, 3f, 3f, 1f, true, 1f, 0f), R(true, 3f, 3f, 1f, true, 1f, 0f), R(true, 3f, 3f, 1f, true, 1f, 0f), R(true, 3f, 3f, 1f, true, 1f, 0f), R(true, 3f, 3f, 1f, true, 1f, 0f) },
                // Leather
                new[] { R(true, 3f, 3f, 2f, false, 1f, 0f), R(true, 3f, 3f, 1f, false, 1f, 0f), R(true, 3f, 3f, 1f, false, 1f, 0f), R(true, 3f, 3f, 1f, false, 1f, 0f), R(true, 3f, 3f, 1f, false, 1f, 0f), R(true, 3f, 3f, 1f, false, 1f, 0f), R(true, 3f, 3f, 1f, false, 1f, 0f) },
                // Chainmail
                new[] { R(true, 4f, 4f, 3f, false, 1f, 0f), R(true, 4f, 4f, 1.75f, false, 1f, 0f), R(true, 4f, 4f, 1.75f, false, 1f, 0f), R(true, 4f, 4f, 1.75f, false, 1f, 0f), R(true, 4f, 4f, 1.75f, false, 1f, 0f), R(true, 4f, 4f, 1.75f, false, 1f, 0f), R(true, 4f, 4f, 1.75f, false, 1f, 0f) },
                // Plate
                new[] { R(true, 4f, 4f, 2.25f, false, 1f, 0.1f), R(true, 4f, 4f, 2.25f, false, 1f, 0f), R(true, 4f, 4f, 2.25f, false, 1f, 0f), R(true, 4f, 4f, 2.25f, false, 1f, 0f), R(true, 4f, 4f, 2.25f, false, 1f, 0f), R(true, 4f, 4f, 2.25f, false, 1f, 0f), R(true, 4f, 4f, 2.25f, false, 1f, 0f) }
            },
            // DamageModifier_PierceArrow
            new[] {
                // Flesh
                new[] { R(true, 3f, 3f, 5f, true, 1f, 0f), R(true, 3f, 3f, 5f, true, 1f, 0.5f), R(true, 3f, 3f, 1.5f, true, 1f, 0f), R(true, 3f, 3f, 1f, true, 1f, 0f), R(true, 3f, 3f, 0.75f, true, 1f, 0f), R(true, 3f, 3f, 0.75f, true, 1f, 0f), R(true, 3f, 3f, 0.75f, true, 1f, 0f) },
                // Leather
                new[] { R(false, 6f, 6f, 4f, false, 1f, 0f), R(true, 3f, 3f, 3f, true, 1f, 0.5f), R(true, 3f, 3f, 3f, true, 1f, 0f), R(true, 3f, 3f, 2f, true, 1f, 0f), R(true, 3f, 3f, 2f, true, 1f, 0f), R(true, 3f, 3f, 2f, true, 1f, 0f), R(true, 3f, 3f, 2f, true, 1f, 0f) },
                // Chainmail
                new[] { R(false, 6f, 9f, 3f, false, 1f, 0.5f), R(false, 6f, 9f, 3f, false, 1f, 0.5f), R(true, 6f, 9f, 5f, false, 1f, 0.5f), R(true, 6f, 9f, 3f, false, 1f, 0f), R(true, 6f, 9f, 3f, false, 1f, 0f), R(true, 6f, 9f, 3f, false, 1f, 0f), R(true, 6f, 9f, 3f, false, 1f, 0f) },
                // Plate
                new[] { R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f), R(false, 28f, 1f, 1f, false, 1f, 0f) }
            },
            // DamageModifier_Slash
            new[] {
                // Flesh
                new[] { R(true, 3f, 3f, 1f, true, 1f, 0.003f), R(true, 3f, 3f, 1f, true, 1f, 0.003f), R(true, 3f, 3f, 1f, true, 1f, 0.003f), R(true, 3f, 3f, 1f, true, 1f, 0.003f), R(true, 3f, 3f, 1f, true, 1f, 0.003f), R(true, 3f, 3f, 1f, true, 1f, 0.003f), R(true, 3f, 3f, 1f, true, 1f, 0.003f) },
                // Leather
                new[] { R(false, 3f, 7.5f, 1f, false, 1f, 0f), R(true, 3f, 8f, 3f, false, 0.5f, 0.003f), R(true, 3f, 8f, 3f, false, 0.5f, 0.003f), R(true, 3f, 5f, 1f, false, 0.5f, 0.003f), R(true, 3f, 5f, 1f, false, 0.5f, 0.003f), R(true, 3f, 5f, 1f, false, 0.5f, 0.003f), R(true, 3f, 5f, 1f, false, 0.5f, 0.003f) },
                // Chainmail
                new[] { R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f) },
                // Plate
                new[] { R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f), R(false, 8f, 1f, 1f, false, 1f, 0f) }
            },
            // DamageModifier_SlashAxe
            new[] {
                // Flesh
                new[] { R(true, 3f, 6f, 6f, true, 1f, 0f), R(true, 3f, 6f, 1.5f, true, 1f, 0f), R(true, 3f, 6f, 1.5f, true, 1f, 0f), R(true, 3f, 6f, 1f, true, 1f, 0f), R(true, 3f, 6f, 1f, true, 1f, 0f), R(true, 3f, 6f, 1f, true, 1f, 0f), R(true, 3f, 6f, 1f, true, 1f, 0f) },
                // Leather
                new[] { R(false, 4f, 14f, 2f, false, 1f, 0f), R(true, 3f, 10f, 1.6f, true, 1f, 0f), R(true, 3f, 10f, 1.6f, true, 1f, 0f), R(true, 3f, 8f, 1.4f, true, 1f, 0f), R(true, 3f, 8f, 1.4f, true, 1f, 0f), R(true, 3f, 8f, 1.4f, true, 1f, 0f), R(true, 3f, 8f, 1.4f, true, 1f, 0f) },
                // Chainmail
                new[] { R(false, 4f, 7.5f, 1f, false, 1f, 0f), R(true, 3f, 12f, 2f, false, 1f, 0f), R(true, 3f, 12f, 2f, false, 1f, 0f), R(true, 3f, 10f, 1.8f, false, 1f, 0f), R(true, 3f, 10f, 1.8f, false, 1f, 0f), R(true, 3f, 10f, 1.8f, false, 1f, 0f), R(true, 3f, 10f, 1.8f, false, 1f, 0f) },
                // Plate
                new[] { R(false, 3f, 1f, 1f, false, 1f, 0f), R(false, 3f, 1f, 1f, false, 1f, 0f), R(false, 3f, 1f, 1f, false, 1f, 0f), R(false, 3f, 1f, 1f, false, 1f, 0f), R(false, 3f, 1f, 1f, false, 1f, 0f), R(false, 3f, 1f, 1f, false, 1f, 0f), R(false, 3f, 1f, 1f, false, 1f, 0f) }
            }
        };

        public static PenetrationModifier ModifierFor(PenetrationProfile profile)
        {
            switch (profile)
            {
                case PenetrationProfile.DaggerPierce: return PenetrationModifier.PierceFine;
                case PenetrationProfile.WarpickPierce: return PenetrationModifier.PierceWarpick;
                case PenetrationProfile.Arrow: return PenetrationModifier.PierceArrow;
                case PenetrationProfile.DaggerSlash:
                case PenetrationProfile.SaberSlash: return PenetrationModifier.Slash;
                case PenetrationProfile.AxeSlash1H:
                case PenetrationProfile.AxeSlash2H: return PenetrationModifier.SlashAxe;
                default: return PenetrationModifier.Pierce;
            }
        }

        // tier: B&S item tier 0..6 (Bannerlord ItemTiers Tier1..Tier6 = 0..5).
        // Bannerlord files lamellar and scale under Plate; by design choice
        // they use the B&S leather rows, pierced along the whole blade (B&S
        // plate stops every blade, its mail stops a sword at 20 cm).
        // Bannerlord chainmail keeps the B&S chainmail rows.
        public static Row Get(PenetrationProfile profile, PenetrationMaterial material, int tier)
        {
            if (material == PenetrationMaterial.Plate) material = PenetrationMaterial.Leather;
            tier = Math.Max(0, Math.Min(6, tier));
            Row row = Table[(int)ModifierFor(profile)][(int)material][tier];
            return row.Valid ? row : R(false, float.PositiveInfinity, float.PositiveInfinity, 1, false, 1, 0);
        }
    }
}
