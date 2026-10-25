using System;
using TaleWorlds.Core;

namespace BannerlordVR.VR.Physics
{
    public static class EnemyPhysicsSettings
    {
        // enemy_physics (default 1) builds the physics in (its patches apply
        // at load; 0 leaves every one out);
        // the VR panel's Enemy physics box (enemy_physics_active, saved by the
        // panel) switches it in game: off, the mod's own physical combat
        // fights, motion controls as they are. It needs the panel's motion
        // controls (VrPhysicalCombat) on as well.
        public static readonly bool Configured = VrConfig.Bool("enemy_physics", true);
        public static bool Active = VrConfig.Bool("enemy_physics_active", true);
        public static bool Enabled => Configured && Active && VrPhysicalCombat.Enabled;
        public static readonly int Capacity = Math.Max(1, Math.Min(12, VrConfig.Int("enemy_physics_max_agents", 6)));
        public static readonly int ScanBudget = Math.Max(16, Math.Min(512, VrConfig.Int("enemy_physics_scan_budget", 128)));
        public static readonly float Radius = Read("enemy_physics_radius", 4, 2, 8);
        public static readonly float ExitRadius = Radius + 1;
        // On horseback too: every hit speed is already the blade's relative
        // to the body it meets, so a charge strikes as hard as it should.
        public static readonly bool Mounted = VrConfig.Bool("enemy_physics_mounted", true);
        // Enemy riders are physical bodies too (cuts, stabs, blood, parries,
        // grabs of the head or a hand); a ragdoll is never started in the
        // saddle: a destabilizing blow or a hard pull unseats them instead.
        public static readonly bool Riders = VrConfig.Bool("enemy_physics_riders", true);
        // B&S RevealDecal: a wound decal where each cut and stab landed.
        public static readonly bool Wounds = VrConfig.Bool("enemy_physics_wounds", false);
        // B&S choking: a hand on a living enemy's neck strangles it.
        public static readonly bool Choke = VrConfig.Bool("enemy_physics_choke", false);
        // Stab model on trial: tog (Tales of Glory 4's WepScript stab, see
        // TogStab) or bs (Blade & Sorcery's damager penetration).
        public static readonly bool TogStab = !string.Equals(VrConfig.String("enemy_physics_stab_model", "tog"), "bs", StringComparison.OrdinalIgnoreCase);
        // Every mission mode with melee in it: field battles, but also
        // tournaments, duels and hideout stealth, the game's and mods' alike.
        public static bool Fighting(MissionMode mode) =>
            mode == MissionMode.Battle || mode == MissionMode.Duel || mode == MissionMode.Tournament || mode == MissionMode.Stealth;
        public static readonly float Spring = Read("enemy_physics_grab_spring", 180, 10, 500);
        public static readonly float Damping = Read("enemy_physics_grab_damping", 22, 1, 80);
        public static readonly float MaxForce = Read("enemy_physics_max_force", 350, 20, 800);
        public static readonly float BreakDistance = Read("enemy_physics_break_distance", 0.65f, 0.2f, 1.2f);
        public static readonly bool WeaponDamage = VrConfig.Bool("enemy_physics_weapon_damage", true);
        public static readonly bool LivingRagdolls = VrConfig.Bool("enemy_physics_living_ragdoll", true);
        public static readonly bool ActiveBalance = VrConfig.Bool("enemy_physics_active_balance", false);
        // Blade & Sorcery creature drive: every ragdoll body pinned to the
        // animated pose, destabilization, fall and stand-up. Supersedes the
        // pelvis/chest balance when both are enabled.
        public static readonly bool ActiveRagdoll = VrConfig.Bool("enemy_physics_active_ragdoll", true);
        // Runtime probe variant: a virtual hand grabs the neck and lifts it.
        public static readonly bool ActiveProbeGrab = VrConfig.Bool("enemy_physics_active_probe_grab", false);
        // Probe load: total number of enemies running the cycle at once.
        public static readonly int ActiveProbeBodies = Math.Max(1, Math.Min(12, VrConfig.Int("enemy_physics_active_probe_bodies", 1)));
        // Custom battle test range: player on foot, AI frozen, five dummies.
        public static readonly bool Sandbox = VrConfig.Bool("enemy_physics_sandbox", false);
        // Measure the blade capsules against the drawn bodies (HitboxProbe):
        // a diagnostic, off unless set (the fitted hitboxes do not need it).
        public static readonly bool HitboxProbe = VrConfig.Bool("enemy_physics_hitbox_probe", false);
        // Widen the blade capsules to the drawn body, per outfit (HitboxFit).
        public static readonly bool FitHitbox = VrConfig.Bool("enemy_physics_fit_hitbox", true);
        // Measure each outfit's drawn body instead of the table (8-9 ms per
        // armour mesh read: froze big battles).
        public static readonly bool FitHitboxMeasure = VrConfig.Bool("enemy_physics_fit_hitbox_measure", false);
        // Sandbox enemies that keep their AI and come to attack the player.
        public static readonly int SandboxDuelists = Math.Max(0, Math.Min(4, (int)VrConfig.Float("enemy_physics_sandbox_duelists", 1)));
        public static readonly bool RuntimeProbe = VrConfig.Bool("enemy_physics_runtime_probe", false);
        // Logs pushes, ragdoll impacts and parries during normal VR play.
        public static readonly bool Trace = RuntimeProbe || VrConfig.Bool("enemy_physics_trace", false);
        // bodyTurnDirection of the grab handles; off by default (see ActiveRagdollBody).
        // B&S rotation pins through the native torque bridge (enemy_physics_rotation_pins=0 turns them off).
        public static readonly bool RotationPins = VrConfig.Bool("enemy_physics_rotation_pins", true);
        public static readonly bool GrabBodyTurn = VrConfig.Bool("enemy_physics_grab_turn", false);
        // Every hit on a living enemy makes its body physical for the sway.
        public static readonly bool HitPhysics = VrConfig.Bool("enemy_physics_hit_physics", true);
        // Lead the headset may take over a body stopped against an enemy (m); 0 keeps BannerlordVR's walking.
        public static readonly float CloseReach = Read("enemy_physics_close_reach", 1, 0, 1.5f);
        // A body lying on its back gets up with the game's own rise animation.
        // Blend from the physical pose into the animation on release (off:
        // the helmet stayed on the animated head while the body blended).
        public static readonly bool ReleaseBlend = VrConfig.Bool("enemy_physics_release_blend", false);
        public static readonly bool AnimatedRise = VrConfig.Bool("enemy_physics_animated_rise", true);
        // While its body is physical an agent's own capsule stays at the spot
        // it was held at; close it so it does not block the player there.
        public static readonly bool CloseHeldCapsule = VrConfig.Bool("enemy_physics_close_held_capsule", true);
        // A sword thrown point first plants in the enemy it meets.
        public static readonly bool ThrownBladesStick = VrConfig.Bool("enemy_physics_thrown_blades_stick", true);
        // Restart a nearby corpse's death timer so the engine keeps it physical.
        // Off: lifting corpses never worked (their ragdoll would not restart).
        public static readonly bool KeepNearCorpses = VrConfig.Bool("enemy_physics_keep_near_corpses", false);
        // A body stays a physical object this long after its death, near the
        // player, for every weapon (B&S: a body within reach is physical).
        public static readonly float CorpseSeconds = Read("enemy_physics_corpse_seconds", 8, 0, 30);
        // Turn a thrown blade point first in flight (B&S flyRotationSpeed).
        public static readonly bool ThrownAlign = VrConfig.Bool("enemy_physics_thrown_align", false);
        // Shields stop the shown weapon and hand, not only turn the weapon.
        public static readonly bool SolidShields = VrConfig.Bool("enemy_physics_solid_shields", true);
        // Enemy blades stop the player's blade and parry by contact (B&S).
        public static readonly bool SolidWeapons = VrConfig.Bool("enemy_physics_solid_weapons", true);
        // Enemy shield plates from the shield's own entity box, not VrBlade's fit.
        // Off: the weapon entity's box sat 0.93 m and 77 deg off the shield drawn.
        public static readonly bool EntityShieldPlates = VrConfig.Bool("enemy_physics_entity_shield_plates", false);
        // A blade pressing an enemy shield pushes it aside, as in B&S.
        public static readonly bool ShieldDeflect = VrConfig.Bool("enemy_physics_shield_deflect", true);
        // Hits are tested on the drawn capsules the blade collides with, not
        // the engine's limb colliders.
        public static readonly bool DrawnHits = VrConfig.Bool("enemy_physics_drawn_hits", true);
        // A sword's swing bites into the part and slides along the cut (B&S
        // slash joint) instead of only striking.
        public static readonly bool SlashCuts = VrConfig.Bool("enemy_physics_slash_cuts", true);
        // A severed limb is folded rigidly into the body in the engine's bone
        // records (LimbHide): the head sinks into the chest, a forearm, hand,
        // calf or foot is laid onto the bone above it.
        public static readonly bool LimbHide = VrConfig.Bool("enemy_physics_limb_hide", true);
        // The free hand can take a one-handed weapon by its blade (half-sword).
        public static readonly bool HalfSword = VrConfig.Bool("enemy_physics_half_sword", true);
        // An enemy dying on the blade drops its weapon and shield (B&S).
        public static readonly bool DropOnDeath = VrConfig.Bool("enemy_physics_drop_on_death", true);
        // A blunt blow to the head knocks the enemy out (B&S Discombobulate).
        public static readonly bool Knockout = VrConfig.Bool("enemy_physics_knockout", true);
        // A severed head's helmet flies off as a physical object.
        public static readonly bool SeveredProps = VrConfig.Bool("enemy_physics_severed_props", true);
        // Only watch the cut bone's record instead of folding it (in-game probe).
        public static readonly bool LimbWatch = VrConfig.Bool("enemy_physics_limb_watch", false);
        // A neck cut fast enough hides the head (meshless head armour). Off:
        // rebuilding the visuals without the head crashed the game when the
        // corpse went ragdoll (19 Oct 2026, docs/physical-enemies.md).
        public static readonly bool Decapitation = VrConfig.Bool("enemy_physics_decapitation", false);
        // A helmet gives the head its thickness in the blade collisions.
        public static readonly bool HelmetVolume = VrConfig.Bool("enemy_physics_helmet_volume", true);
        public static readonly bool ArmStabDisarm = VrConfig.Bool("enemy_physics_arm_stab_disarm", true);
        public static readonly bool CadenceProbe = VrConfig.Bool("enemy_physics_cadence_probe", false);
        public static readonly bool CadenceRagdoll = VrConfig.Bool("enemy_physics_cadence_ragdoll", false);
        public static readonly bool NeckCuts = VrConfig.Bool("enemy_physics_neck_cut", true);
        public static readonly bool Bleeding = VrConfig.Bool("enemy_physics_bleeding", false);
        public static readonly bool WoundProbe = VrConfig.Bool("enemy_physics_wound_probe", false);
        public static readonly float BudgetMilliseconds = Read("enemy_physics_budget_ms", 1.5f, .1f, 5);
        private static float Read(string key, float fallback, float min, float max)
        {
            float v = VrConfig.Float(key, fallback);
            return InteractionMath.IsFinite(v) ? Math.Max(min, Math.Min(max, v)) : fallback;
        }
    }
}
