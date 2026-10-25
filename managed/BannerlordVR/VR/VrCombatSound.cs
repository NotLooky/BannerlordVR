using System;
using System.Collections.Generic;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The vanilla combat sounds our physical combat does not get for free
    /// (TEST 240).
    ///
    /// WHAT THE ENGINE ALREADY PLAYS
    ///
    /// A hit that lands through Agent.RegisterBlow gets its sound inside
    /// HandleBlow: BlowWeaponRecord.GetHitSound picks cut/pierce/blunt by the
    /// blow's own DamageType and magnitude, the "Armor Type" parameter layers
    /// the armor material, and Mission.MakeSound plays it. Physical hits have
    /// gone through that path since TEST 236, so the hit half is the engine's.
    ///
    /// WHAT IT CANNOT PLAY
    ///
    /// A swing whoosh and a weapon clash hang off the attack ANIMATION in
    /// vanilla - events the animation fires. The player plays no attack
    /// animation (that is the point of physical combat), so nothing fires
    /// them. They are ordinary events in the game's own bank, so this class
    /// plays them from the physics instead, at the same instants vanilla
    /// would:
    ///
    ///   * a swing window opening -> event:/mission/combat/swing/<kind>;
    ///   * a block (weapon or shield)   -> event:/mission/combat/impact/
    ///     <striking physics material>/<struck physics material>.
    ///
    /// THE MATERIAL NAMES ARE THE EVENT PATHS
    ///
    /// The item XMLs use physics_material="metal_weapon", "wood_weapon",
    /// "metal_shield" and "wood_shield", and the impact events are named
    /// exactly impact/<striker>/<struck> for every pair of those. Nothing is
    /// guessed: WeaponComponentData.PhysicsMaterial is used verbatim when it
    /// names one of the four, and only a material outside that set is mapped
    /// by its metal/wood word.
    ///
    /// A sound is never worth a throw inside the damage path, so every play
    /// is behind a try and a missing event simply does not play. The first
    /// play of each path is logged once, which is how the log proves the
    /// engine accepted the event id.
    /// </summary>
    public static class VrCombatSound
    {
        /// <summary>combat_sound = 0 plays none of these; the engine's own hit
        /// sounds are untouched either way.</summary>
        public static bool Enabled { get; } = VrConfig.Bool("combat_sound", true);

        /// <summary>The swing whoosh. combat_swing_sound = 0 silences it.</summary>
        private static readonly bool SwingSound = VrConfig.Bool("combat_swing_sound", true);

        /// <summary>Hand m/s below which a window does not deserve a whoosh.
        /// Without this every walk-by window (combat_swing_speed = 1.0) would
        /// hiss; a real swing reads 3-12 m/s at the grip (TEST 237b).</summary>
        private static readonly float SwingSoundSpeed = VrConfig.Float("combat_swing_sound_speed", 3f);

        /// <summary>Hand m/s a window must PEAK past to earn a whoosh. The
        /// window itself opens at combat_swing_speed, which is below this, so
        /// opening cannot tell a swing from a nudge. The signal the caller
        /// measures is tracked controller velocity, so game locomotion and stair
        /// height corrections do not count.</summary>
        public static float SwingGate => SwingSoundSpeed;

        internal static float SwingPeak(float previous, bool open, BvrHandState hand)
        {
            float speed = hand.IsActive != 0 ? VrMath.ToBannerlord(hand.LinearVelocity).Length : 0f;
            return Math.Max(open ? previous : 0f, speed);
        }

        /// <summary>Logs the first play of each event path.</summary>
        private static readonly bool Probe = VrConfig.Bool("combat_sound_probe", true);

        private static readonly Dictionary<string, int> Paths =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static readonly HashSet<string> Logged =
            new HashSet<string>(StringComparer.Ordinal);

        private const string SwingPath = "event:/mission/combat/swing/";
        private const string Impact = "event:/mission/combat/impact/";

        /// <summary>The whoosh a swing window gets once its peak passes
        /// SwingGate. Kind comes from the weapon and the motion: a thrust is a
        /// pierce, a two-handed or polearm weapon is large, a dagger is small,
        /// and everything else is medium - the four swing events the combat set
        /// has.</summary>
        public static void Swing(Mission mission, Agent agent, VrBlade.Segment blade,
                                 WeaponComponentData usage, bool thrust)
        {
            if (!SwingSound)
                return;

            Play(mission, SwingPath + Kind(usage, thrust),
                 blade.Valid ? blade.TipWorld : agent.Position, agent.Index, -1);
        }

        /// <summary>A block or parry: the attacker's weapon against the thing
        /// that stopped it. The striker's material is first because the event
        /// names the moving object first: impact/<striker>/<struck>.</summary>
        public static void Clash(Mission mission, Vec3 at, string strikerMaterial,
                                 string struckMaterial, int strikerIndex, int struckIndex)
        {
            Play(mission, Impact + strikerMaterial + "/" + struckMaterial, at,
                 strikerIndex, struckIndex);
        }

        /// <summary>The physics material of an item, as the impact events spell
        /// it. Falls back by the metal/wood word for anything outside the four
        /// shipped names, and to wood for nothing at all.</summary>
        public static string Material(WeaponComponentData usage, bool shield)
        {
            string fallback = shield ? "wood_shield" : "wood_weapon";

            if (usage == null)
                return fallback;

            string material = usage.PhysicsMaterial;
            if (string.IsNullOrEmpty(material))
                return fallback;

            if (material == "metal_weapon" || material == "wood_weapon"
                || material == "metal_shield" || material == "wood_shield")
                return material;

            if (material.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0)
                return shield ? "metal_shield" : "metal_weapon";

            if (material.IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0)
                return shield ? "wood_shield" : "wood_weapon";

            return fallback;
        }

        private static string Kind(WeaponComponentData usage, bool thrust)
        {
            if (thrust)
                return "pierce";

            if (usage != null)
            {
                if (usage.WeaponClass == WeaponClass.Dagger
                    || usage.WeaponClass == WeaponClass.ThrowingKnife)
                    return "small";

                if (usage.IsTwoHanded || usage.IsPolearm)
                    return "large";
            }

            return "med";
        }

        internal static void Play(Mission mission, string path, Vec3 at, int related1, int related2)
        {
            if (!Enabled || mission == null)
                return;

            try
            {
                if (!Paths.TryGetValue(path, out int id))
                {
                    id = SoundEvent.GetEventIdFromString(path);
                    Paths[path] = id;
                }

                if (id < 0)
                    return;

                mission.MakeSound(id, at, soundCanBePredicted: false, isReliable: true,
                                  related1, related2);

                if (Probe && Logged.Add(path))
                {
                    VrLog.Info("TEST 240 sound: '" + path + "' is playing - the engine's own "
                               + "combat event, fired from the physics instead of an animation. "
                               + "combat_sound = 0 turns these off.");
                }
            }
            catch (Exception)
            {
                // A sound is never worth a throw inside the damage path.
            }
        }
    }
}
