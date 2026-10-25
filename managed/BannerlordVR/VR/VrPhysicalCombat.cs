using System;
using System.Diagnostics;
using System.Globalization;
using BannerlordVR.Interop;
using BannerlordVR.Patches;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using UsageDirection = TaleWorlds.MountAndBlade.Agent.UsageDirection;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Physical melee: the blade you swing is the thing that hits.
    ///
    /// WHY THE LAST TWO ATTEMPTS PROVED NOTHING
    ///
    /// The first one did proximity maths against the grip and wrote Agent.Health
    /// by reflection. The engine ignores a health write for death and reactions,
    /// so the victim's health moved and nothing else did. The hit sounds it
    /// produced came from the vanilla attack flags it also set, not from its
    /// hits - so the one thing that seemed to confirm it was the one thing that
    /// had nothing to do with it. The second one (TEST 236-OLD in the live cfg,
    /// reverted) drove the gesture into the engine's attack flags and let the
    /// engine's animation do the damage. Both bypassed the only seam that
    /// matters and neither could have worked.
    ///
    /// THE SEAM IS Agent.RegisterBlow(Blow, in AttackCollisionData), AND IT IS
    /// PUBLIC
    ///
    ///   Agent.RegisterBlow -> HandleBlow:
    ///     - subtracts InflictedDamage from Health
    ///     - calls Die() when health drops below 1 (ragdoll, kill feed, loot)
    ///     - plays the weapon's own hit sound through BlowWeaponRecord
    ///     - calls the native HandleBlowAux for the stagger and the flinch
    ///
    /// TEST 236 proved it end to end: health 100.0 -> 31.0 on a 69 damage blow,
    /// the delta exactly InflictedDamage. Nothing here needs an animation.
    ///
    /// PHASE 1 (TEST 237): THE CONTROLLER'S SWING, ON THE ENGINE'S COLLISION
    ///
    /// Per hand, per frame:
    ///
    ///   * the blade is measured and its world segment read (VrBlade),
    ///   * the tip's speed is followed. Above combat_swing_speed a swing window
    ///     opens; it closes after combat_swing_close seconds of slow or
    ///     combat_swing_max at the outside - the plan's ~0.15 s / 1 s,
    ///   * while it is open, TWO of the engine's own raycasts run each frame:
    ///     along the blade (grip -> tip) and along the tip's path since the last
    ///     frame. Both are the engine's Mission.RayCastForClosestAgentsLimbs,
    ///     with the blade's own measured radius as the ray thickness. Friends,
    ///     the player and mounts are dropped; enemies only,
    ///   * one hit per victim per window; a cleave continues to the next only
    ///     while the ENGINE's own DecideWeaponCollisionReaction called the last
    ///     one SlicedThrough and its momentum has not run out (TEST 263) - the
    ///     same rule a real vanilla cleave runs on, not a flat count,
    ///   * the strike type is read from the motion: tip velocity parallel to
    ///     the blade (dot > 0.75) is a thrust, anything else a swing,
    ///   * and it lands through Mission.MeleeHitCallback (TEST 263, through
    ///     MeleeHitPatch.Invoke) - the mod decides only where things touched;
    ///     armor, perks, difficulty, the bone multiplier, damage, knockback and
    ///     the rest are the engine's, exactly as a real collision's would be.
    ///
    /// The player never plays an attack animation: this class never sets an
    /// attack flag, it only reads the controller and swings what is in the hand.
    ///
    /// TEST 303 REPLACED PHASE 1's WINDOW AND RAYS. The engine's limb ray never
    /// returned a hit in a whole session (35 of 35 brush checks "also misses"),
    /// and the window missed every blade that arrived after it closed. The
    /// blade is now tested against each body's own skeleton every tick, with no
    /// window and no gesture to wait for - see Contacts and BodyOf. The strike
    /// type is what the weapon can deliver first and the motion second (TEST
    /// 306, ChooseThrust).
    ///
    /// PHASE 3 (TEST 239/240/241): A GUARD, A SHIELD, AND THE SOUNDS
    ///
    ///   * the block test measures the attacker's weapon from the bone the
    ///     engine attached it to (BlowWeaponRecord.BoneNoToAttach), not from
    ///     the parked slot entity TEST 238 used - see BeforePlayerBlow. The
    ///     blade against that line is a real weapon-against-weapon test;
    ///   * a shield guards the same way - its plate against the same line -
    ///     but only while it is raised to chest level (combat_shield_* keys);
    ///   * an enemy's raised shield stops the physical hit before the body does;
    ///   * the swing whoosh and the material-correct clash are played from the
    ///     physics, because the animations that fire them in vanilla never run
    ///     (VrCombatSound).
    ///
    /// PHASE 4 (TEST 264/265): BLADE-ON-BLADE BLOCKING
    ///
    /// Decompiling Mission.MeleeHitCallback (ilspycmd against the shipped DLL,
    /// see [[bl2vr-decompile-engine-api]]) settled a question the plan had
    /// left open: the engine does NOT read the defender's action state to
    /// decide Blocked/Parried/ChamberBlocked for itself. CollisionResult,
    /// AttackBlockedWithShield and CorrectSideShieldBlock are INPUTS the
    /// caller already decided - true for a real engine collision (native code
    /// upstream of the callback did that geometry), and true for us: nothing
    /// downstream infers a block from `victim.GetCurrentActionType`.
    ///
    ///   * TEST 264 (your block): VrMelee.GuardPass's weapon-hand guard now
    ///     triggers on real blade contact against a nearby attacking enemy's
    ///     TEST-262-fitted blade (TryWeaponBladeContact), not a hand-position
    ///     zone - the shield keeps the zone test, unchanged. Contact writes
    ///     the same DefendXxx + DefendBlock bits as before, so the primary
    ///     resolution is still the engine's own, native, once the Defend
    ///     action lands in time. The safety net is BeforeBlow's own TEST
    ///     238-242 geometric cancel test (the player's blade against the
    ///     ATTACKER's real collision-derived weapon line) - previously
    ///     skipped outright whenever VrMelee was on ("the guard is the
    ///     engine's own block state now"), which was only true once the
    ///     Defend bit had actually landed. It runs every time now: it only
    ///     ever fires on a blow the engine already decided was NOT blocked
    ///     with nonzero damage, so it can only catch what native resolution
    ///     missed, never double up on what it caught.
    ///
    ///   * TEST 265 (enemies guard against you): GuardStops (a victim's own
    ///     weapon-Defend action) is real blade contact against their fitted
    ///     blade now, not a reach zone. ShieldStops keeps "raised in front =
    ///     guard" exactly as it was - shields are not fitted, and the plate
    ///     test was already precise. Strike() no longer returns early on
    ///     either: it sets CollisionResult = Blocked (and
    ///     AttackBlockedWithShield/CorrectSideShieldBlock for a shield) on
    ///     the same AttackCollisionData a clean hit builds, then invokes the
    ///     real MeleeHitCallback exactly as TEST 263 already does - so a
    ///     block's stagger, a shield's own durability damage, and the combat
    ///     log are the engine's, not a hand-built approximation.
    ///
    /// Both are gated on TEST 262's enemy-blade fit maturing for whatever
    /// weapon class is actually in a fight: a class with fewer than
    /// EnemyFitMinSamples real engine hits has no fitted blade yet, and
    /// neither TEST 264's contact test nor TEST 265's GuardStops can answer
    /// for it - the guard simply does not fire until the fit is ready, the
    /// same as TEST 262 always intended.
    /// </summary>
    public static class VrPhysicalCombat
    {
        /// <summary>
        /// Physical collision combat: the player's swings hit, their raised
        /// shield blocks, arrows stop on it. combat = 0 leaves all of it to the
        /// engine's own triggers and animations, exactly as before this class
        /// existed. The VR panel's checkbox flips this live (TEST 244).
        /// </summary>
        public static bool Enabled { get; private set; } = VrConfig.Bool("combat", true);

        /// <summary>
        /// The VR panel's switch (TEST 244). Off closes any open swing window and
        /// clears the measured blades at once; the hands, the weapons and the
        /// game's own attacks are untouched. The Harmony patches stay applied and
        /// this runtime gate does the work, because a toggle that needs a game
        /// restart is not a toggle.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            if (enabled == Enabled)
                return;

            Enabled = enabled;
            CloseAll();

            if (!enabled)
            {
                for (int role = 0; role < Roles; role++)
                    _live[role] = default(VrBlade.Segment);
            }
        }

        // --- the Phase 0 debug strike (TEST 236) ---------------------------

        /// <summary>The debug strike. Numpad 7/8/9 are the keys neither the
        /// game's own bindings nor the weapon tuner touch. Pressed once per
        /// press - Input.IsKeyPressed is edge-triggered.</summary>
        private static readonly string TestKey = VrConfig.String("combat_test_key", "Numpad9");

        /// <summary>How far away an enemy may be to be picked as the target.</summary>
        private static readonly float TestRange = VrConfig.Float("combat_test_range", 6f);

        /// <summary>0 uses the weapon's own SwingDamage; anything above 0 is
        /// used verbatim, to make a test hit that is certain to show.</summary>
        private static readonly float TestDamage = VrConfig.Float("combat_test_damage", 0f);

        // --- the swing (TEST 237) ------------------------------------------

        /// <summary>Hand speed in m/s that opens a swing window. The GRIP is the
        /// tracked point (TEST 237b): the tip sits 1.2 m from the wrist, so its
        /// rotation noise reads as tens of m/s; the grip lands exactly where the
        /// controller is and reads the motion that is actually yours.
        ///
        /// 2.0 (TEST 242b, was 1.0): at 1.0 a reach, a handover or a shrug opened
        /// a window, so attacks misfired out of ordinary movement. A real swing
        /// reads 3-12 m/s at the grip.</summary>
        private static readonly float SwingSpeed = VrConfig.Float("combat_swing_speed", 2.0f);

        /// <summary>Hand m/s the grip must fall below to start closing the window.</summary>
        private static readonly float SwingStop = VrConfig.Float("combat_swing_stop", 0.4f);

        /// <summary>Seconds of slow before the window closes.</summary>
        private static readonly float SwingClose = VrConfig.Float("combat_swing_close", 0.15f);

        /// <summary>Hard limit on one window, however long the hand keeps moving.</summary>
        private static readonly float SwingMax = VrConfig.Float("combat_swing_max", 1.0f);

        /// <summary>Seconds after a swing window OPENS before the same hand may
        /// open another one (TEST 242b). Without it a hand that keeps moving
        /// above combat_swing_stop re-opens the moment the window closes, so one
        /// wave reads as several attacks. Per hand: the other hand is free. A
        /// window that lives longer than the cooldown owes nothing after it
        /// closes.</summary>
        private static readonly float SwingCooldown = VrConfig.Float("combat_swing_cooldown", 0.5f);

        // combat_ref_speed, combat_speed_min/max, combat_head_mult, combat_neck_mult
        // and combat_cleave/combat_cleave_falloff are gone (TEST 263): damage,
        // the body-part multiplier and the cleave falloff are the ENGINE's own
        // now (Mission.MeleeHitCallback, through MeleeHitPatch.Invoke), not a
        // flat multiplier chain read off the weapon's raw SwingDamage. See
        // Strike and the Swing.Momentum field below for what cleave uses
        // instead: colReaction == SlicedThrough and momentum > 0, the same rule
        // the engine's own cleave runs on.

        /// <summary>Tip movement in one frame above this is a draw, a sheathe or
        /// a teleport rather than a swing, and must not open a window.</summary>
        private static readonly float Teleport = VrConfig.Float("combat_teleport_m", 1.0f);

        /// <summary>
        /// Exponential smoothing for SPEED and DIRECTION, in milliseconds; 0
        /// turns it off. Rays always use the raw blade.
        ///
        /// WHY IT EXISTS (TEST 237 RESULT): the tip sits 1.2 m from the wrist,
        /// so tracking noise at the hand is amplified by that lever. The raw
        /// tip signal read 30-117 m/s peaks and never dropped below the stop
        /// speed, so every window lived its full second and no hit ever read as
        /// a thrust (dot ~0.1). Speed and direction now come from the GRIP -
        /// the point the controller actually is - and this filter only takes
        /// the last of the tracking jitter off it. A real swing is a ramp and a
        /// filter does not attenuate a ramp, only delays it.
        /// </summary>
        private static readonly float SmoothMs = VrConfig.Float("combat_smooth_ms", 40f);

        /// <summary>Logs swing windows, not just hits.</summary>
        private static readonly bool Probe = VrConfig.Bool("combat_probe", true);

        // --- blocking (TEST 238) -------------------------------------------

        /// <summary>combat_block = 0 lets every enemy blow land, which is the
        /// behaviour before this key existed.</summary>
        private static readonly bool Block = VrConfig.Bool("combat_block", true);

        /// <summary>How close your blade must be to the blow's impact point to
        /// catch it, in metres. THE FALLBACK ONLY (TEST 239): used when the
        /// engine reported no attach bone for the blow (a kick, a punch). A
        /// blow with a weapon is tested against the weapon's own line, where
        /// the impact is one end of the segment.</summary>
        private static readonly float BlockRadius = VrConfig.Float("combat_block_radius", 0.18f);

        /// <summary>
        /// If the enemy's swing direction runs ALONG your blade axis by more
        /// than this (|dot|), it glides down the blade and is not stopped.
        ///
        /// DEFAULT 1.0 - OFF. TEST 238d: the plan's glide test was meant to
        /// refuse blows that slide along the blade, but every real block landed
        /// with glide anywhere from 0.10 to 0.87 and the refusals were 0.90-0.99,
        /// including one at 0.14 m - well inside the radius and exactly where the
        /// player was holding the blade. The number is decided by the enemy's
        /// animation against your grip, which the player can neither see nor
        /// control, so a refusal reads as "my sword was right there and nothing
        /// happened". The radius is the honest test (TEST 238b measured a clean
        /// 0.14-to-0.32 m gap); this gate is a stricter optional knob.
        /// </summary>
        private static readonly float BlockGlide = VrConfig.Float("combat_block_glide", 1.0f);

        /// <summary>Logs every enemy blow that is NOT blocked, with the nearest
        /// blade distance and the reason. Without it the log cannot tell "no
        /// attack happened" from "the block failed", which is the whole tuning
        /// question (TEST 238b).</summary>
        private static readonly bool BlockProbe = VrConfig.Bool("combat_block_probe", true);

        /// <summary>Your blade radius plus their blade radius plus this is the
        /// contact window. 0.08 (TEST 242): the attacker's blade now comes from
        /// the engine's own collision data, so this is tracking lag and a model
        /// that measures oddly, not the old chord's geometric error - TEST 239e
        /// needed 0.15 because the line itself was a guess. Back to 0.15 if real
        /// blocks stop registering; 0.03 is the next step once the log shows
        /// contacts at 0.0-0.05.</summary>
        private static readonly float BlockSlack = VrConfig.Float("combat_block_slack", 0.08f);

        /// <summary>The attacker's blade radius assumed for their weapon line
        /// when the item's own mesh cannot be measured. TEST 242: the measured
        /// radius is used when there is one (VrBlade.TryGetWeaponShape); this is
        /// only the fallback.</summary>
        private static readonly float BlockTheirRadius =
            VrConfig.Float("combat_block_their_radius", 0.05f);

        // --- shields (TEST 241) ---------------------------------------------

        /// <summary>"Raised to chest level": how far the shield's centre may
        /// sit above or below the holder's chest bone and still count as a
        /// guard. A shield in a raised block sits at the sternum; one carried
        /// at the hip or over the back fails this band.</summary>
        private static readonly float ShieldLow = VrConfig.Float("combat_shield_low", -0.30f);
        private static readonly float ShieldHigh = VrConfig.Float("combat_shield_high", 0.35f);

        /// <summary>How far in front of the chest the plate must be held to
        /// count as raised - and how far out it may reach.</summary>
        private static readonly float ShieldAhead = VrConfig.Float("combat_shield_ahead", 0.05f);
        private static readonly float ShieldReach = VrConfig.Float("combat_shield_reach", 0.90f);

        /// <summary>Added to the shield radius when the plate is tested against
        /// the incoming weapon line. A shield is a disc, so this is a wider
        /// window than a blade's by construction.</summary>
        private static readonly float ShieldSlack = VrConfig.Float("combat_shield_slack", 0.10f);

        /// <summary>Play the engine's clash sound on a block or parry.</summary>
        private static readonly bool BlockSound = VrConfig.Bool("combat_block_sound", true);

        /// <summary>Your hand speed, and the blow's direction, that turn a block
        /// into a parry: opposing motion above this.</summary>
        private static readonly float ParrySpeed = VrConfig.Float("combat_parry_speed", 3.0f);

        /// <summary>Haptics. The native side now applies a real
        /// xrApplyHapticFeedback pulse (TEST 238c); a runtime that refuses it,
        /// or a session that is not ready, reports once rather than per block.</summary>
        private static readonly bool Haptics = VrConfig.Bool("combat_haptic", true);
        private static readonly float HapticAmplitude = VrConfig.Float("combat_haptic_amplitude", 1.0f);
        private static readonly float HapticMs = VrConfig.Float("combat_haptic_ms", 150f);

        /// <summary>TEST 261: a pulse when the blade lands a hit. It scales
        /// with the damage, from combat_haptic_hit_min on a graze to the block
        /// amplitude on a heavy blow, so the hands say how the swing went
        /// without reading the log.</summary>
        private static readonly bool HapticHit = VrConfig.Bool("combat_haptic_hit", true);
        private static readonly float HapticHitMin = Math.Max(0f, Math.Min(1f, VrConfig.Float("combat_haptic_hit_min", 0.35f)));

        /// <summary>A short, sharp pulse when the enemy's guard or shield stops
        /// the blade (TEST 261) - the feel of hitting a stopped thing.</summary>
        private static readonly bool HapticClash = VrConfig.Bool("combat_haptic_clash", true);
        private static readonly float HapticClashAmp = Math.Max(0f, Math.Min(1f, VrConfig.Float("combat_haptic_clash_amp", 0.5f)));
        private static readonly float HapticClashMs = Math.Max(1f, VrConfig.Float("combat_haptic_clash_ms", 60f));

        /// <summary>A pulse when the ENGINE resolves one of your own blocks
        /// (TEST 261). The mod's own block test pulses in Guard; this covers the
        /// guard state, where the engine cancels the blow itself.</summary>
        private static readonly bool HapticBlock = VrConfig.Bool("combat_haptic_block", true);

        // --- accuracy, guards and targets (TEST 242) -----------------------

        /// <summary>The blade's poses from the last few frames are sampled for a
        /// block, not only the current one. A fast guard can meet the weapon
        /// between two samples; the pose either side of the meeting is what
        /// catches it. This is the age limit for a swept pose, in milliseconds.</summary>
        private static readonly float SweepMs = VrConfig.Float("combat_block_sweep_ms", 60f);

        /// <summary>How well a collision-data direction must explain the
        /// contact's along-blade distance before it replaces the attach-bone
        /// chord: the perpendicular miss of the grip from the line plus half the
        /// along-distance error, in metres.</summary>
        private static readonly float LineSlack = VrConfig.Float("combat_block_line_slack", 0.25f);

        /// <summary>Enemies in their own defend action (a raised shield or
        /// weapon) stop your physical hit (TEST 242; blade contact since
        /// TEST 265). The engine never sees the player's swing as an attack,
        /// so the AI will not RAISE a new block in reaction; a guard that is
        /// already up is what this catches.</summary>
        private static readonly bool EnemyGuard = VrConfig.Bool("combat_enemy_guard", true);

        // combat_enemy_guard_slack is gone (TEST 265): GuardStops tests real
        // blade contact against the victim's TEST-262-fitted weapon now, not a
        // reach zone, so it shares BlockSlack with every other blade-vs-blade
        // test instead of its own separate tolerance.

        /// <summary>Whether a raised shield stops arrows and bolts (TEST 243).
        /// This is the vanilla rule: a shield blocks missiles, a weapon does
        /// not unless a perk says so, and nothing blocks a horse charge. The
        /// engine cannot resolve it for the player because the player never
        /// plays a block animation - the shield is where the VR hand puts it,
        /// and only the mod knows that is a guard.</summary>
        private static readonly bool BlockArrows = VrConfig.Bool("combat_block_arrows", true);

        /// <summary>Added to the shield radius when a missile's flight path is
        /// tested against it. The shield is a sphere read out of its plate, so
        /// this absorbs the thickness and the tracking between frames.</summary>
        private static readonly float BlockArrowSlack =
            VrConfig.Float("combat_block_arrow_slack", 0.20f);

        /// <summary>Whether a physical strike may land on friends and neutrals
        /// (TEST 242). 1, the request, matches nothing the engine would do: in
        /// single player the engine cancels melee damage on friends and neutrals
        /// itself (Mission.CancelsDamageAndBlocksAttackBecauseOfNonEnemyCase).
        /// 0 restores that rule: enemies only.
        ///
        /// TEST 261: 0 is the default now, and the VR panel's checkbox (or this
        /// key) turns it on. Missions that say no friendly fire - ForceNoFriendlyFire,
        /// IsFriendlyMission - refuse it either way; see Target.</summary>
        private static bool _friendlyFire = VrConfig.Bool("combat_friendly_fire", false);

        /// <summary>The live value, for the panel checkbox.</summary>
        public static bool FriendlyFire => _friendlyFire;

        /// <summary>The panel's switch (TEST 261). Applied live: the very next
        /// physical hit uses it, and nothing else about physical combat changes.
        /// </summary>
        public static void SetFriendlyFire(bool enabled)
        {
            if (enabled == _friendlyFire)
                return;

            _friendlyFire = enabled;
            VrLog.Info("VR panel: friendly fire is now " + (enabled ? "ON" : "off") + ". "
                       + (enabled
                           ? "Physical swings can land on friends and neutrals."
                           : "Only enemies can be hit - the vanilla single-player rule."));
        }

        /// <summary>Whether the blade may land on horses. The player's own mount
        /// is always excluded. 0 leaves mounts out entirely.</summary>
        private static readonly bool HitMounts = VrConfig.Bool("combat_hit_mounts", true);

        // --- TEST 303: the blade is always live ----------------------------

        /// <summary>Added to the measured blade radius in the contact test, so a
        /// blade that is edge-on to a body still catches. The measured radius is
        /// the blade's thin half-width; this is the tracking gap and the edge.</summary>
        private static readonly float RayPad = Math.Max(0f, VrConfig.Float("combat_ray_pad", 0.02f));

        /// <summary>In-between blade poses tested per tick (TEST 261) - the
        /// minimum: SweepCuts cuts a tick as finely as the tip's travel plus a
        /// galloping body's needs (TEST 295), up to 8.</summary>
        private static readonly int SweepSteps =
            Math.Max(0, Math.Min(3, VrConfig.Int("combat_sweep_steps", 1)));

        /// <summary>
        /// TEST 303: how fast the touching point of the blade must move against
        /// the body it touches for the touch to be a blow, in m/s. The blade is
        /// live every tick - no swing window, no gesture to wait for - so this is
        /// the one thing between resting the sword on someone and cutting them.
        /// It is read from the smoothed hand and the blade's smoothed turn, not
        /// from one tick's raw pose step (TEST 237: the raw tip read tens of m/s
        /// while the hand moved at two).
        ///
        /// 4.0 (TEST 303b, was 1.5): "it registers very effortless". 272 hits
        /// read 1.5-44 m/s; a quarter of the sword hits were under 5, the taps.
        /// 6.0 (TEST 303c): still effortless - half of the next session's 91
        /// hits came with the hand under 2 m/s, carried by the lever (below).
        /// This is the gate for a CUT; a stab has its own.
        /// </summary>
        private static readonly float HitSpeed = Math.Max(0f, VrConfig.Float("combat_hit_speed", 6.0f));

        /// <summary>
        /// TEST 303e: a CUT also needs the ARM to move this fast, in m/s - the
        /// hand against the body it strikes (a horse counts), or the stroke's
        /// peak so far, whichever is more. The 02:40 session's body hits split
        /// cleanly on it: every wrist flick that landed had the arm at 1.7-2.8
        /// m/s (the flick made the blade 6-11 m/s), every real swing 3 or more.
        /// The blade's speed alone cannot tell them apart - real swings landed
        /// at 6-17 m/s too.
        /// </summary>
        private static readonly float ArmSpeed = Math.Max(0f, VrConfig.Float("combat_hit_arm_speed", 3.0f));

        /// <summary>TEST 303c: the gate for a STAB - the touching point moving
        /// along the blade toward the tip. A push is slower than a cut and is
        /// almost all hand, with no lever to help it.</summary>
        private static readonly float ThrustHitSpeed =
            Math.Max(0f, VrConfig.Float("combat_thrust_hit_speed", 3.0f));

        /// <summary>TEST 303c: the most of the blade's length that counts as a
        /// lever for its turn, in metres. A wrist flick swings a spear's head at
        /// a sword-cut's speed with no effort at all; past this, length adds
        /// reach, not speed.</summary>
        private static readonly float HitLever = Math.Max(0.1f, VrConfig.Float("combat_hit_lever_m", 0.6f));

        /// <summary>TEST 303c: the most bodies one stroke may strike. The same
        /// body struck again within the stroke does not count twice.</summary>
        private static readonly int MaxVictims =
            Math.Max(1, Math.Min(8, VrConfig.Int("combat_max_victims", 3)));

        /// <summary>TEST 303c: the most of the blade's measured radius the
        /// contact test uses, in metres. The measured radius is the mesh box's
        /// half-width - the crossguard, the axe's head, a lance's pennon (0.36 m)
        /// - not the edge, and it reached into bodies the blade never touched.</summary>
        private const float BladeEdge = 0.04f;

        /// <summary>
        /// TEST 303: the same body can be struck again once the blade has been out
        /// of it for half this long AND this long has passed since its last blow,
        /// in milliseconds. A blade left in a body, sawing, or jittering on its
        /// edge is one blow; pulling out and striking again is another.
        ///
        /// TEST 303d (200, was 150): also the least time between ANY two blows
        /// of one hand ("I can hit very fast; make it 0.20 s between each hit").
        /// The one exception is the next body of the same stroke - a cut through
        /// a crowd still reaches combat_max_victims.
        /// </summary>
        private static readonly float RehitMs = Math.Max(0f, VrConfig.Float("combat_rehit_ms", 200f));

        /// <summary>TEST 306: how closely the touching point must move along the
        /// blade, toward the tip, to be a thrust (a cosine). Only asked of a weapon
        /// that can both swing and thrust: one that can do only one of them always
        /// does that one.</summary>
        private static readonly float ThrustDot = VrConfig.Float("combat_thrust_dot", 0.7f);

        /// <summary>Logs a stroke that struck no one while a body was within a
        /// metre of the blade, with the gap - so a miss is a number in the log,
        /// not an argument.</summary>
        private static readonly bool MissProbe = VrConfig.Bool("combat_probe_miss", true);

        // melee_hit_window_ms, combat_brush, combat_brush_radius and
        // combat_contact_speed are gone (TEST 303): there is no window to time
        // and no fallback to need. The blade meets the victim's own skeleton
        // every tick, and a blade held out on a gallop is simply a contact whose
        // speed comes from the horse.

        /// <summary>TEST 295: the fastest a body is assumed to move (a galloping
        /// horse, m/s), for how finely a frame's sweep is cut so a charging
        /// target cannot slip between two blade poses.</summary>
        private const float GallopSpeed = 12f;

        // -------------------------------------------------------------------

        private const int Roles = 2;

        /// <summary>The mission tick can run more than once per rendered frame;
        /// a sample shorter than this is the same controller pose twice and
        /// would read as a hand that stopped dead.</summary>
        private const float MinSample = 0.003f;

        private sealed class Swing
        {
            public bool Have;
            public Vec3 PrevRawTip;

            /// <summary>The grip a frame ago, for the mid-blade sweep (TEST 261).</summary>
            public Vec3 PrevRawGrip;

            /// <summary>The HAND in the AGENT's frame (world minus the agent's
            /// position), so walking or riding does not read as a swing: the
            /// body carries the hand with it and cancels. TEST 239b, after the
            /// horse's own speed kept a sword whoosh playing while riding.</summary>
            public Vec3 PrevRelGrip;
            public Vec3 SmoothRel;

            /// <summary>TEST 303: the blade's direction a tick ago, and its
            /// smoothed turn (rad/s, about this vector) - the touching point's
            /// speed is the hand's plus this turn times the lever from the hand.</summary>
            public Vec3 PrevAxis;
            public Vec3 Omega;

            /// <summary>TEST 303: the bodies this hand's blade is in.</summary>
            public readonly TouchSet Touches = new TouchSet();

            // The stroke. Only the whoosh and the log line run on it now; no
            // hit waits for it (TEST 303).
            public bool Open;
            public bool Sounded;
            public bool Thrust;
            public long OpenedAt;
            public long SlowSince;
            public long CooldownUntil;
            public float Peak;
            public float SoundPeak;
            public int Hits;

            /// <summary>TEST 303c: the bodies this stroke has struck, for
            /// combat_max_victims.</summary>
            public readonly int[] Struck = new int[8];
            public int StruckCount;

            /// <summary>TEST 303d: this hand's next blow waits until then.</summary>
            public long NextBlowAt;

            /// <summary>The closest any body's capsule came to the blade's edge
            /// during the stroke, for the near-miss line.</summary>
            public float NearGap;
            public string NearName;
        }

        /// <summary>
        /// TEST 303: which bodies one hand's blade is in, and when each was last
        /// struck. A touch is a blow when the blade is moving (the caller's speed
        /// gate) and it is a NEW touch: the body is not already being cut, the
        /// blade has been out of it for half the re-hit time, and the re-hit time
        /// has passed since its last blow. A touch that comes back sooner is the
        /// same contact, still going. Clock-free (the caller passes `now`), so
        /// the test suite can step it.
        /// </summary>
        public sealed class TouchSet
        {
            private const int Size = 16;

            private readonly int[] _agent = new int[Size];
            private readonly long[] _hitAt = new long[Size];
            private readonly long[] _leftAt = new long[Size];
            private readonly bool[] _in = new bool[Size];
            private readonly bool[] _seen = new bool[Size];
            private int _count;

            /// <summary>One body touched this tick. True when it is a blow.</summary>
            public bool Touch(int agent, long now, bool fast, long rehit)
            {
                int i = Find(agent);
                if (i >= 0)
                {
                    _seen[i] = true;
                    if (_in[i])
                        return false;

                    if (now - _leftAt[i] < rehit / 2 || now - _hitAt[i] < rehit)
                    {
                        _in[i] = true;
                        return false;
                    }
                }

                if (!fast)
                    return false;

                if (i < 0)
                {
                    // ponytail: sixteen bodies inside one blade at once is a pile
                    // of corpses; the seventeenth waits for a slot.
                    if (_count == Size)
                        return false;

                    i = _count++;
                    _agent[i] = agent;
                }

                _hitAt[i] = now;
                _leftAt[i] = 0L;
                _in[i] = true;
                _seen[i] = true;
                return true;
            }

            /// <summary>After a tick's touches: a body not touched is out of the
            /// blade, and one struck longer than `forget` ago and out is dropped.</summary>
            public void EndTick(long now, long forget)
            {
                for (int i = _count - 1; i >= 0; i--)
                {
                    if (!_seen[i] && _in[i])
                    {
                        _in[i] = false;
                        _leftAt[i] = now;
                    }

                    _seen[i] = false;

                    if (!_in[i] && now - _hitAt[i] > forget)
                    {
                        _count--;
                        _agent[i] = _agent[_count];
                        _hitAt[i] = _hitAt[_count];
                        _leftAt[i] = _leftAt[_count];
                        _in[i] = _in[_count];
                        _seen[i] = _seen[_count];
                    }
                }
            }

            public void Clear()
            {
                _count = 0;
            }

            private int Find(int agent)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_agent[i] == agent)
                        return i;
                }

                return -1;
            }
        }

        private static readonly Swing[] _swings = { new Swing(), new Swing() };
        private static readonly string[] RoleName = { "main", "off" };

        /// <summary>This tick's blade per hand, kept for the block prefix: an
        /// enemy blow is registered mid-tick, and this is the player's blade as
        /// of the last seam before it.</summary>
        private static readonly VrBlade.Segment[] _live = new VrBlade.Segment[Roles];
        private static readonly Vec3[] _liveMove = new Vec3[Roles];
        private static readonly float[] _liveSpeed = new float[Roles];

        /// <summary>TEST 314: the hand, its world velocity (the body's carry
        /// included) and the blade's turn, for VrEnemyGuard's look ahead.</summary>
        private static readonly Vec3[] _liveHand = new Vec3[Roles];
        private static readonly Vec3[] _liveVel = new Vec3[Roles];
        private static readonly Vec3[] _liveOmega = new Vec3[Roles];

        /// <summary>What each hand is holding, for the clash material and for
        /// naming the guard in the block log (TEST 240/241).</summary>
        private static readonly WeaponComponentData[] _liveUsage = new WeaponComponentData[Roles];

        /// <summary>A raised shield per hand (TEST 241). A hand with a shield
        /// has no blade, so the two never both exist for one hand.</summary>
        private static readonly VrBlade.Shield[] _shield = new VrBlade.Shield[Roles];
        private static readonly WeaponComponentData[] _shieldUsage = new WeaponComponentData[Roles];

        /// <summary>The last few blade poses per hand (TEST 242). The block test
        /// runs on the current pose plus these, so a guard that met the weapon
        /// between two samples still counts as a contact.</summary>
        private const int SweepSlots = 3;

        private sealed class Sweep
        {
            public readonly VrBlade.Segment[] Segments = new VrBlade.Segment[SweepSlots];
            public readonly long[] Stamps = new long[SweepSlots];
            public int Next;
        }

        private static readonly Sweep[] _sweep = { new Sweep(), new Sweep() };

        private static bool _hapticsReported;
        private static bool _hapticsSent;
        private static bool _loggedBlockPulse;
        private static readonly long[] _hapticAt = new long[Roles];

        /// <summary>The last blocked blow on the player, for BlockedLogPatch
        /// (TEST 242): victimMine + this damage inside a second is the log entry
        /// the popup would have been built from. Consumed on a match.</summary>
        private static long _blockedAt;
        private static int _blockedDamage;

        private static long _stamp;
        private static bool _broken;
        private static int _failures;
        private static bool _loggedReady;
        private static bool _loggedSwing;

        /// <summary>TEST 261: a failure in the hit path no longer ends physical
        /// combat for the session. The first failure is logged with its
        /// exception; only after this many does the system stand down, because
        /// a per-frame throw at frame rate is worse than no hits.</summary>
        private const int MaxFailures = 5;

        private static void Failed(Exception ex, string what)
        {
            if (_failures == 0)
                VrLog.Error(what + " - the first time only; it keeps running.", ex);

            if (++_failures < MaxFailures)
                return;

            _broken = true;
            VrLog.Error("TEST 261: physical combat failed " + MaxFailures
                        + " times; it is off for this session. Melee goes back to the "
                        + "game's own triggers.");
        }

        /// <summary>
        /// One frame, from AfterMissionTick - after the hands and the weapon
        /// have been driven, so the blade read here is this frame's pose. It
        /// runs whether or not the hands are being posed: phase 0's strike must
        /// not depend on that, and a missing skinned weapon falls back to the
        /// engine's own entity (VrBlade).
        /// </summary>
        public static void Tick()
        {
            // TEST 248. Two ways this class runs, and they are not the same
            // thing:
            //
            //   classic   - combat = 1 and melee = 0. Everything it ever did:
            //               swings hit, its block tests cancel blows.
            //   instant   - melee = 1 and melee_instant_hit = 1. Only the STRIKE
            //               half: the physical hit lands at the blade the moment
            //               it touches, while the engine's own attack runs for
            //               the AI and the animation. The DEFENCE half is the
            //               engine's own guard state, raised by VrMelee on a
            //               whole-blade touch; BeforeBlow still runs on every
            //               blow and stops what that guard landed too late for
            //               (TEST 264, TEST 311).
            //
            // TEST 246/247 measured the engine's own swing at ~430 ms from
            // gesture to blow, with most swings missing outright. That is the
            // engine's animation timing and it cannot be sped up from here (the
            // speed multiplier moves neither the ready phase nor the strike), so
            // the blade is the only way to get the damage there on time.
            bool classic = Enabled && !VrMelee.Enabled;
            bool instant = VrMelee.Enabled && VrMelee.InstantStrike;

            if (_broken || (!classic && !instant))
                return;

            try
            {
                long now = Stopwatch.GetTimestamp();
                float dt = _stamp == 0L
                    ? 0f
                    : (float)((now - _stamp) / (double)Stopwatch.Frequency);
                _stamp = now;

                // A hitch or a load must not read as a hand moving at 200 m/s.
                if (dt > 0.1f)
                    dt = 0.1f;

                if (VrKeys.Pressed(TestKey))
                    StrikeNearest();

                Agent agent = Mission.Current?.MainAgent;
                if (agent == null || !agent.IsActive() || !agent.CombatActionsEnabled)
                {
                    CloseAll();
                    VrEnemyGuard.Clear();
                    return;
                }

                MBAgentVisuals visuals = agent.AgentVisuals;
                Skeleton skeleton = visuals != null ? visuals.GetSkeleton() : null;
                if (skeleton == null
                    || !VrHandSlots.Resolve(agent, skeleton, out VrHandSlot main, out VrHandSlot off))
                {
                    CloseAll();
                    return;
                }

                // Two samples closer together than MinSample are one pose; no
                // new sweep runs.
                if (dt < MinSample)
                    return;

                if (!_loggedSwing)
                {
                    _loggedSwing = true;
                    VrLog.Info("TEST 303: physical melee is on. The blade is live every tick: "
                               + "any body it touches at " + HitSpeed.ToString("0.0", CultureInfo.InvariantCulture)
                               + " m/s or more is struck on the bone it touched, again after the blade "
                               + "leaves it for " + RehitMs.ToString("0", CultureInfo.InvariantCulture)
                               + " ms. Damage is the engine's own.");
                }

                if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                    input = default;
                SwingPass(agent, main, now, dt, input);
                SwingPass(agent, off, now, dt, input);

                // TEST 314: every enemy near the blade sees where it is going.
                VrEnemyGuard.Track(agent);

                // TEST 311: the whole-blade contact every tick, on this tick's
                // blade - not only when VrMelee's guard pass asks (it skips a
                // moving hand), so a parry in motion is remembered too.
                if (Block)
                    TryWeaponBladeContact(agent, out _, out _, out _);

                _failures = 0;
            }
            catch (Exception ex)
            {
                Failed(ex, "TEST 237: a physical-combat tick failed");
            }
        }

        /// <summary>One hand: its blade against every body near it (TEST 303),
        /// and the stroke that carries the whoosh and the log line.</summary>
        private static void SwingPass(Agent agent, VrHandSlot slot, long now, float dt, BvrInputState input)
        {
            int role = (int)slot.Role;

            // The previous tick's pose goes on the sweep ring before this tick
            // overwrites it. The block test consults the ring so a blade that
            // met the weapon between two samples still counts (TEST 242).
            VrBlade.Segment previous = _live[role];
            if (previous.Valid)
                PushSweep(role, previous);

            // Cleared first and set again only when this hand is holding a
            // measured melee blade, so the block prefix can never consult a
            // blade from a hand that has nothing in it.
            _live[role] = default(VrBlade.Segment);
            _liveMove[role] = Vec3.Zero;
            _liveSpeed[role] = 0f;
            _liveVel[role] = Vec3.Zero;
            _liveOmega[role] = Vec3.Zero;
            _liveUsage[role] = null;
            _shield[role] = default(VrBlade.Shield);
            _shieldUsage[role] = null;

            if (!slot.Valid || slot.Equipment == EquipmentIndex.None)
            {
                Idle(role, now);
                return;
            }

            WeaponComponentData usage = Held(agent, slot.Equipment);
            if (usage == null)
            {
                Idle(role, now);
                return;
            }

            if (usage.IsShield)
            {
                // A shield does not swing. Its live plate is kept so the block
                // prefix can stop an incoming blow with it (TEST 241); the
                // chest-level rule is applied there, not here.
                if (VrBlade.TryGetShield(agent, slot, out VrBlade.Shield shield))
                {
                    _shield[role] = shield;
                    _shieldUsage[role] = usage;
                }

                Idle(role, now);
                return;
            }

            if (!usage.IsMeleeWeapon)
            {
                Idle(role, now);
                return;
            }

            if (!VrBlade.TryGet(agent, slot, out VrBlade.Segment blade))
            {
                Idle(role, now);
                return;
            }

            // TEST 331b: a weapon gliding into the hand is not in play yet - no
            // blade for the block test, the enemies or the collider, and the
            // swing reseeds from wherever the glide ends.
            if (VrSkinnedWeapon.Gliding(slot.Role))
            {
                _swings[role].Have = false;
                Close(role, now, log: false);
                return;
            }

            _live[role] = blade;
            _liveUsage[role] = usage;

            // TEST 316: the blade in the frame of the hand target it was drawn
            // from, for next tick's collision.
            VrCollide.Learn(slot.Role, blade);

            Swing s = _swings[role];

            Vec3 rawTip = blade.TipWorld;
            Vec3 rawGrip = blade.GripWorld;

            // THE HAND, not the blade's grip end (TEST 306). The copy entity's
            // origin is the item bone the controller holds (VrSkinnedWeapon.
            // Place); GripWorld is the mesh's near end - on a polearm the butt
            // cap, a metre behind the fist. Everything that reads the player's
            // motion reads the hand. The tip and the sweep stay in world space -
            // contact is world - and the sweep keeps the whole weapon, butt too.
            Vec3 hand = blade.World.origin;
            Vec3 relGrip = hand - agent.Position;

            Vec3 axis = rawTip - rawGrip;
            axis = axis.LengthSquared < 1e-8f ? blade.AxisWorld : axis.NormalizedCopy();

            if (!s.Have)
            {
                s.Have = true;
                s.PrevRawTip = rawTip;
                s.PrevRawGrip = rawGrip;
                s.PrevRelGrip = relGrip;
                s.SmoothRel = relGrip;
                s.PrevAxis = axis;
                s.Omega = Vec3.Zero;
                s.Open = false;
                return;
            }

            Vec3 rawPrev = s.PrevRawTip;
            Vec3 rawPrevGrip = s.PrevRawGrip;
            s.PrevRawTip = rawTip;
            s.PrevRawGrip = rawGrip;

            Vec3 prevAxis = s.PrevAxis;
            s.PrevAxis = axis;

            // A draw or a teleport moves the HAND metres in one frame RELATIVE
            // TO THE BODY; that is not a swing, and the sweep must not cut a path
            // through everything between the two places.
            float gripMoved = (relGrip - s.PrevRelGrip).Length;
            s.PrevRelGrip = relGrip;

            if (gripMoved > Teleport)
            {
                s.SmoothRel = relGrip;
                s.Omega = Vec3.Zero;
                s.Touches.Clear();
                Close(role, now, log: false);
                return;
            }

            // Speed and direction come from the smoothed HAND - the point the
            // controller is at - and the blade's turn from its smoothed axis.
            float alpha = SmoothAlpha(dt);
            Vec3 prevSmoothGrip = s.SmoothRel;
            s.SmoothRel = Lerp(prevSmoothGrip, relGrip, alpha);

            float moved = (s.SmoothRel - prevSmoothGrip).Length;
            float speed = dt > 0f ? moved / dt : 0f;
            Vec3 move = moved > 1e-6f
                ? (s.SmoothRel - prevSmoothGrip) * (1f / moved)
                : Vec3.Zero;

            // TEST 303: the small-angle turn over the tick, filtered like the
            // hand. A tick that saw the same pose twice reads no turn and the
            // next one double; the filter is what makes that one smooth turn.
            if (dt > 0f)
                s.Omega = Lerp(s.Omega, Vec3.CrossProduct(prevAxis, axis) * (1f / dt), alpha);

            _liveSpeed[role] = speed;
            _liveMove[role] = move;
            _liveHand[role] = hand;
            _liveVel[role] = move * speed + Carried(agent);
            _liveOmega[role] = s.Omega;

            // Stair corrections can spike the drawn hand's speed without a real swing.
            s.SoundPeak = VrCombatSound.SwingPeak(s.SoundPeak, s.Open,
                slot.Controller == VrHand.Left ? input.Left : input.Right);

            // TEST 303: THE BLADE IS LIVE EVERY TICK. Every body it crosses at
            // speed is struck, whatever the hand was doing a moment ago - no
            // window to open first, no gesture to wait for.
            Contacts(agent, slot, usage, blade, s, move * speed, hand,
                     rawPrevGrip, rawGrip, rawPrev, rawTip, dt, now);

            // The stroke: the whoosh the attack animation would have fired
            // (TEST 240) and the per-stroke log line. It opens on the hand's own
            // speed in every mode now; nothing about a hit waits for it.
            if (!s.Open)
            {
                if (now >= s.CooldownUntil && speed >= SwingSpeed)
                    Open(role, Vec3.DotProduct(move, axis) > ThrustDot, speed, now);

                return;
            }

            if (s.Peak < speed)
                s.Peak = speed;

            // Plays the frame the stroke's PEAK passes the sound gate, not when it
            // opens: it opens at combat_swing_speed, which is below the gate, so
            // the open alone cannot tell a swing from a nudge.
            if (!s.Sounded && s.SoundPeak >= VrCombatSound.SwingGate)
            {
                s.Sounded = true;
                VrCombatSound.Swing(Mission.Current, agent, blade, usage, s.Thrust);
            }

            if (speed >= SwingStop)
                s.SlowSince = 0L;
            else if (s.SlowSince == 0L)
                s.SlowSince = now;

            long slowFor = s.SlowSince == 0L ? 0L : now - s.SlowSince;
            if ((s.SlowSince != 0L && slowFor >= Seconds(SwingClose))
                || now - s.OpenedAt >= Seconds(SwingMax))
                Close(role, now, log: true);
        }

        private static float SmoothAlpha(float dt)
        {
            if (SmoothMs <= 0f)
                return 1f;

            float tau = SmoothMs * 0.001f;
            return dt / (tau + dt);
        }

        private static Vec3 Lerp(Vec3 from, Vec3 to, float t)
        {
            return from + (to - from) * t;
        }

        /// <summary>A stroke starts: the whoosh and the log line follow it.</summary>
        private static void Open(int role, bool thrust, float speed, long now)
        {
            Swing s = _swings[role];

            s.Open = true;
            s.Sounded = false;
            s.Thrust = thrust;
            s.OpenedAt = now;
            s.SlowSince = 0L;
            s.Peak = speed;
            s.Hits = 0;
            s.StruckCount = 0;
            s.NearGap = float.MaxValue;
            s.NearName = null;

            // Measured from the stroke's start (TEST 242b), so one continuous
            // wave does not whoosh as several. It gates the sound only.
            s.CooldownUntil = now + Seconds(SwingCooldown);
        }

        private static void Close(int role, long now, bool log)
        {
            Swing s = _swings[role];
            if (!s.Open)
                return;

            float seconds = (float)((now - s.OpenedAt) / (double)Stopwatch.Frequency);

            if (log && Probe && s.Hits > 0)
            {
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 303 stroke {0}: {1} hit(s), peak {2:F1} m/s over {3:F2} s.",
                    RoleName[role], s.Hits, s.Peak, seconds));
            }
            else if (log && MissProbe && s.NearGap < 1f)
            {
                // Only when someone was within a metre: a stroke at the air is
                // not a miss, and the log should not say it is.
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 303 near miss: the {0} hand's stroke peaked at {1:F1} m/s over {2:F2} s "
                    + "and struck no one. Closest: '{3}', {4:F2} m short of a touch (blade "
                    + "radius and combat_ray_pad already counted). A touch below "
                    + "combat_hit_speed, or a body already being cut, also ends here.",
                    RoleName[role], s.Peak, seconds, s.NearName ?? "?", Math.Max(0f, s.NearGap)));
            }

            s.Open = false;
            s.Sounded = false;
            s.Thrust = false;
            s.SlowSince = 0L;
            s.Hits = 0;
        }

        /// <summary>The hand holds no blade this tick: the stroke ends, and the
        /// last pose is forgotten so the blade's return cannot sweep a phantom
        /// path from wherever it last was (TEST 303 - every tick is live now).</summary>
        private static void Idle(int role, long now)
        {
            VrCollide.Forget((HandRole)role);
            Close(role, now, log: false);
            _swings[role].Have = false;
            _swings[role].Touches.Clear();
        }

        /// <summary>TEST 314: one hand's blade and how it is moving - for the
        /// enemies, who guard against where it is going.</summary>
        internal static bool TryMotion(int role, out VrBlade.Segment blade, out Vec3 hand, out Vec3 velocity,
                                       out Vec3 omega, out bool canThrust)
        {
            blade = _live[role];
            hand = _liveHand[role];
            velocity = _liveVel[role];
            omega = _liveOmega[role];

            WeaponComponentData usage = _liveUsage[role];
            canThrust = usage != null && usage.ThrustDamage > 0 && usage.ThrustDamageType != DamageTypes.Invalid;
            return blade.Valid && usage != null && _swings[role].Have;
        }

        private static void CloseAll()
        {
            long now = Stopwatch.GetTimestamp();
            Idle(0, now);
            Idle(1, now);
        }

        /// <summary>One blade pose onto a hand's sweep ring (TEST 242).</summary>
        private static void PushSweep(int role, VrBlade.Segment segment)
        {
            Sweep sweep = _sweep[role];
            int slot = sweep.Next;
            sweep.Segments[slot] = segment;
            sweep.Stamps[slot] = Stopwatch.GetTimestamp();
            sweep.Next = (slot + 1) % SweepSlots;
        }

        /// <summary>Whether a sweep-ring pose is young enough to count.</summary>
        private static bool SweepFresh(int role, int slot, long now)
        {
            Sweep sweep = _sweep[role];
            if (!sweep.Segments[slot].Valid || sweep.Stamps[slot] == 0L)
                return false;

            return now - sweep.Stamps[slot] <= Seconds(SweepMs * 0.001f);
        }

        /// <summary>
        /// TEST 303: the blade against every body near it, every tick. The blade
        /// at the previous tick's pose, this tick's, and the in-between poses of
        /// the turn between them (SweepTip, SweepCuts) is tested against each
        /// body's own skeleton (BodyOf), in that body's frame - each pose moves
        /// with the body over its age (TEST 295), so a charging body cannot pass
        /// between two poses. A touch is a blow when the touching point moves at
        /// combat_hit_speed or more against the body and the body is not already
        /// being cut by this hand (TouchSet).
        ///
        /// WHY NOT THE ENGINE'S RAY ANY MORE: the 2026-10-01 session logged 35
        /// "TEST 263 brush check" lines and every one read "a direct ray along
        /// the same segment also misses" - Mission.RayCastForGivenAgentsLimbs
        /// never returned a hit, so the brush (a feet-to-eye line, always the
        /// chest) carried every blow that session landed, and a blade that went
        /// past the shoulder of that line missed. The skeleton is what is drawn.
        /// </summary>
        private static void Contacts(Agent agent, VrHandSlot slot, WeaponComponentData usage,
                                     VrBlade.Segment blade, Swing s, Vec3 handVelocity, Vec3 hand,
                                     Vec3 prevGrip, Vec3 grip, Vec3 prevTip, Vec3 tip,
                                     float dt, long now)
        {
            Mission mission = Mission.Current;
            if (mission == null || dt <= 0f)
                return;

            int role = (int)slot.Role;
            int steps = SweepCuts(SweepSteps, (tip - prevTip).Length, dt);
            float pad = Math.Min(blade.Radius, BladeEdge) + RayPad;
            Vec3 axis = blade.AxisWorld;
            bool canThrust = usage.ThrustDamage > 0 && usage.ThrustDamageType != DamageTypes.Invalid;
            Vec2 mid = ((grip + tip) * 0.5f).AsVec2;
            float reach = (tip - grip).Length * 0.5f + 2f;
            Vec3 carrier = Carried(agent);
            int pending = 0;

            foreach (Agent candidate in mission.Agents)
            {
                // Flat distance (TEST 295): Position is the ground point, and a
                // blade at saddle height is two metres above anyone's feet.
                // TEST 421: distance before Target's native IsEnemyOf call.
                if (candidate == null || !candidate.IsActive()
                    || (candidate.Position.AsVec2 - mid).LengthSquared > reach * reach
                    || !Target(agent, candidate))
                    continue;

                Body body = BodyOf(candidate);

                // TEST 316: the shield in their hand is solid wherever it is held,
                // and the blade meeting it is a blow on the shield.
                VrBlade.Plate plate = default(VrBlade.Plate);
                bool havePlate = candidate.IsHuman && VrBlade.TryGetEnemyPlate(candidate, out plate) && plate.Valid;

                if (body == null && !havePlate)
                    continue;

                Vec3 carried = Carried(candidate);
                Vec3 shift = carried * dt;

                int touched = -1;
                float touchT = float.MaxValue;
                float touchAlong = 0f;
                float nearest = float.MaxValue;

                for (int c = 0; body != null && c < body.Count; c++)
                {
                    float limit = body.Capsules[c].Radius + pad;

                    if (SweepContact(prevGrip, prevTip, grip, tip, steps, shift,
                                     body.Capsules[c].A, body.Capsules[c].B, limit,
                                     out float t, out float along, out float gap)
                        && t < touchT)
                    {
                        touched = c;
                        touchT = t;
                        touchAlong = along;
                    }

                    if (gap - limit < nearest)
                        nearest = gap - limit;
                }

                // The plate first, if the blade met it no later than the body.
                float plateT = 0f;
                float plateAlong = 0f;
                bool onShield = havePlate
                                && SweepPlate(prevGrip, prevTip, grip, tip, steps, shift, plate, pad,
                                              out plateT, out plateAlong)
                                && plateT <= touchT;
                if (onShield)
                {
                    touchT = plateT;
                    touchAlong = plateAlong;
                }

                if (s.Open && nearest < s.NearGap)
                {
                    s.NearGap = nearest;
                    s.NearName = candidate.Name;
                }

                if (touched < 0 && !onShield)
                    continue;

                // Where the blade met the body, at the moment it did, in the
                // body's frame.
                Vec3 moveBack = shift * (1f - touchT);
                Vec3 g = prevGrip + (grip - prevGrip) * touchT + moveBack;
                Vec3 e = SweepTip(prevGrip, prevTip, grip, tip, touchT) + moveBack;
                Vec3 point = g + (e - g) * touchAlong;

                // How fast that point met the body: the hand, the blade's turn
                // about the hand, and whatever carries each side (a horse). The
                // turn's lever stops at combat_hit_lever_m (TEST 303c).
                Vec3 lever = point - hand;
                float leverLength = lever.Length;
                if (leverLength > HitLever)
                    lever = lever * (HitLever / leverLength);

                Vec3 velocity = handVelocity + Vec3.CrossProduct(s.Omega, lever) + carrier - carried;
                float speed = velocity.Length;

                // A stab - the point moving along the blade toward the tip - has
                // its own, lower gate; only for a weapon that can stab (TEST
                // 303e: an axe poked at 4.1 m/s slipped through it as a swing).
                bool stab = canThrust && speed > 1e-3f
                            && Vec3.DotProduct(velocity, axis) > ThrustDot * speed;

                // TEST 303e: a cut also needs the arm behind it.
                float arm = (handVelocity + carrier - carried).Length;
                if (s.Open && s.Peak > arm)
                    arm = s.Peak;

                bool fast = stab ? speed >= ThrustHitSpeed : speed >= HitSpeed && arm >= ArmSpeed;

                // TEST 303c: at most combat_max_victims bodies in one stroke.
                bool known = s.Open && Array.IndexOf(s.Struck, candidate.Index, 0, s.StruckCount) >= 0;
                if (!known && s.Open && s.StruckCount >= MaxVictims)
                    continue;

                // TEST 303d: combat_rehit_ms between this hand's blows - unless it
                // is the next body of the same stroke. Skipped before Touch, so a
                // blade still moving through the body lands when the time is up.
                if (now < s.NextBlowAt && (known || !s.Open))
                    continue;

                // The shield is its own touch: a blade resting on it is one blow,
                // and coming off it onto the body is another.
                int key = onShield ? ~candidate.Index : candidate.Index;
                if (!s.Touches.Touch(key, now, fast, Seconds(RehitMs * 0.001f)))
                    continue;

                // A blow always belongs to a stroke, so the cap holds even for a
                // stroke the hand's own speed never opened.
                if (!s.Open)
                    Open(role, stab, handVelocity.Length, now);

                s.NextBlowAt = now + Seconds(RehitMs * 0.001f);

                if (!known && s.StruckCount < s.Struck.Length)
                    s.Struck[s.StruckCount++] = candidate.Index;

                // Struck after the loop: a blow can kill, and a death must not
                // reshape mission.Agents under this foreach.
                if (pending < _pending.Length)
                {
                    _pending[pending].Victim = candidate;
                    _pending[pending].Capsule = onShield ? default(Capsule) : body.Capsules[touched];
                    _pending[pending].Point = point;
                    _pending[pending].Velocity = velocity;
                    _pending[pending].Shield = onShield;
                    _pending[pending].Plate = plate;
                    pending++;
                }
            }

            s.Touches.EndTick(now, Seconds(2f));

            for (int i = 0; i < pending; i++)
            {
                Pending p = _pending[i];
                _pending[i].Victim = null;

                if (p.Victim.IsActive())
                {
                    Strike(mission, agent, slot, usage, blade, s, p.Velocity, hand, p.Victim,
                           p.Capsule.Bone, p.Capsule.Part, p.Point, p.Shield, p.Plate);
                }
            }

            if (HitObjects && pending == 0)
                StrikeObject(mission, agent, slot, usage, blade, s, handVelocity, hand, carrier,
                             prevGrip, grip, prevTip, tip, canThrust, Math.Max(0.04f, pad), now);
        }

        /// <summary>TEST 641: gates, rams, towers, barricades - any scene object
        /// with a DestructableComponent. combat_hit_objects = 0 turns it off.</summary>
        private static readonly bool HitObjects = VrConfig.Bool("combat_hit_objects", true);

        /// <summary>Where along the blade (0 hand, 1 tip) the tick's travel is
        /// ray-cast against the scene.</summary>
        private static readonly float[] ObjectRays = { 0.45f, 0.75f, 1f };

        private static int _woodMaterial = -2;

        /// <summary>
        /// TEST 641: "hitting the gates with weapons needs to do proper damage".
        /// With melee_engine_swing = 0 the player plays no attack animation, so
        /// the engine's own swing collision - the only thing that ever hit a gate -
        /// never runs, and the blade test above only knows agents. Now the
        /// blade's travel this tick is ray-cast against the scene, and a crossing
        /// into an object that has a DestructableComponent (itself or a parent)
        /// lands through the REAL MeleeHitCallback with no victim and that entity
        /// - vanilla's own path: entity damage multiplier (cut x0.8, pierce x0.1,
        /// BonusAgainstShield x1.2), OnEntityHit, the gate's hit points, its
        /// destruction states and particles, and the combat log. The speed gates
        /// are the body's (combat_hit_speed / _arm_speed / combat_thrust_hit_speed).
        /// </summary>
        private static void StrikeObject(Mission mission, Agent agent, VrHandSlot slot, WeaponComponentData usage,
                                         VrBlade.Segment blade, Swing s, Vec3 handVelocity, Vec3 hand, Vec3 carrier,
                                         Vec3 prevGrip, Vec3 grip, Vec3 prevTip, Vec3 tip, bool canThrust, float thick,
                                         long now)
        {
            Scene scene = mission.Scene;
            if (scene == null || now < s.NextBlowAt)
                return;

            Vec3 axis = blade.AxisWorld;
            for (int k = 0; k < ObjectRays.Length; k++)
            {
                float f = ObjectRays[k];
                Vec3 from = prevGrip + (prevTip - prevGrip) * f;
                Vec3 to = grip + (tip - grip) * f;
                if ((to - from).LengthSquared < 1e-6f)
                    continue;

                // Distance 0 = the ray began inside the body (the blade already in
                // the gate, or being pulled back out): not a new blow.
                if (!scene.RayCastForClosestEntityOrTerrain(from, to, out float distance, out Vec3 point,
                                                            out WeakGameEntity hit, thick,
                                                            BodyFlags.CommonCollisionExcludeFlagsForMissile)
                    || distance <= 1e-4f || !hit.IsValid)
                    continue;

                DestructableComponent target = null;
                WeakGameEntity e = hit;
                for (int up = 0; up < 8 && e.IsValid && target == null; up++, e = e.Parent)
                    target = e.GetFirstScriptOfType<DestructableComponent>();

                if (target == null || target.IsDestroyed)
                    continue;

                // The touching point's speed, as for a body (the object does not move).
                Vec3 lever = point - hand;
                float leverLength = lever.Length;
                if (leverLength > HitLever)
                    lever = lever * (HitLever / leverLength);

                Vec3 velocity = handVelocity + Vec3.CrossProduct(s.Omega, lever) + carrier;
                float speed = velocity.Length;
                bool stab = canThrust && speed > 1e-3f && Vec3.DotProduct(velocity, axis) > ThrustDot * speed;
                float arm = (handVelocity + carrier).Length;
                if (s.Open && s.Peak > arm)
                    arm = s.Peak;

                if (stab ? speed < ThrustHitSpeed : speed < HitSpeed || arm < ArmSpeed)
                    continue;

                if (!s.Open)
                    Open((int)slot.Role, stab, handVelocity.Length, now);

                s.NextBlowAt = now + Seconds(RehitMs * 0.001f);
                s.Hits++;
                LandOnObject(mission, agent, slot, usage, blade, hand, velocity, point, hit, target);
                return;
            }
        }

        private static void LandOnObject(Mission mission, Agent agent, VrHandSlot slot, WeaponComponentData usage,
                                         VrBlade.Segment blade, Vec3 hand, Vec3 velocity, Vec3 point,
                                         WeakGameEntity hit, DestructableComponent target)
        {
            Vec3 axis = blade.AxisWorld;
            Vec3 blowDir = velocity.LengthSquared > 0.01f ? velocity.NormalizedCopy() : axis;
            float motionDot = Vec3.DotProduct(blowDir, axis);
            bool thrust = ChooseThrust(motionDot, usage.SwingDamage, usage.SwingDamageType,
                                       usage.ThrustDamage, usage.ThrustDamageType, ThrustDot);
            StrikeType strikeType = thrust ? StrikeType.Thrust : StrikeType.Swing;
            DamageTypes damageType = thrust ? usage.ThrustDamageType : usage.SwingDamageType;
            UsageDirection attackDirection =
                VrMelee.Enabled && VrMelee.SwingDirection != UsageDirection.None
                    ? VrMelee.SwingDirection
                    : (thrust ? UsageDirection.AttackDown : UsageDirection.AttackLeft);
            sbyte attackBoneIndex = agent.Monster == null
                ? (sbyte)(-1)
                : (slot.Role == HandRole.Main ? agent.Monster.MainHandItemBoneIndex : agent.Monster.OffHandItemBoneIndex);

            // The same contact share on the engine's weapon length Strike uses.
            float handToTip = Vec3.DotProduct(blade.TipWorld - hand, axis);
            float contactFraction = handToTip > 1e-3f
                ? Clamp01(Vec3.DotProduct(point - hand, axis) / handToTip)
                : 1f;
            float offsetZ;
            try { offsetZ = agent.GetCurWeaponOffset().z; } catch (Exception) { offsetZ = 0f; }
            float collisionDistanceOnWeapon = contactFraction * (usage.GetRealWeaponLength() + offsetZ);

            // ponytail: one material for every object (the engine reads it only for
            // "flammable" and "attacks pass through"); a real one needs the hit's own.
            if (_woodMaterial == -2)
            {
                try { _woodMaterial = Math.Max(0, PhysicsMaterial.GetFromName("wood").Index); }
                catch (Exception) { _woodMaterial = 0; }
            }

            AttackCollisionData collisionData = AttackCollisionData.GetAttackCollisionDataForDebugPurpose(
                _attackBlockedWithShield: false,
                _correctSideShieldBlock: false,
                _isAlternativeAttack: false,
                _isColliderAgent: false,
                _collidedWithShieldOnBack: false,
                _isMissile: false,
                _isMissileBlockedWithWeapon: false,
                _missileHasPhysics: false,
                _entityExists: true,
                _thrustTipHit: thrust && contactFraction >= 0.5f,
                _missileGoneUnderWater: false,
                _missileGoneOutOfBorder: false,
                collisionResult: CombatCollisionResult.HitWorld,
                affectorWeaponSlotOrMissileIndex: (int)slot.Equipment,
                StrikeType: (int)strikeType,
                DamageType: (int)damageType,
                CollisionBoneIndex: -1,
                VictimHitBodyPart: BoneBodyPartType.None,
                AttackBoneIndex: attackBoneIndex,
                AttackDirection: attackDirection,
                PhysicsMaterialIndex: _woodMaterial,
                CollisionHitResultFlags: CombatHitResultFlags.NormalHit,
                AttackProgress: MeleeHitPatch.ComputeAttackProgress(strikeType, attackDirection),
                CollisionDistanceOnWeapon: collisionDistanceOnWeapon,
                AttackerStunPeriod: 0f,
                DefenderStunPeriod: 0f,
                MissileTotalDamage: 0f,
                MissileInitialSpeed: 0f,
                ChargeVelocity: 0f,
                FallSpeed: 0f,
                WeaponRotUp: new Vec3(0f, 0f, 1f),
                _weaponBlowDir: blowDir,
                CollisionGlobalPosition: point,
                MissileVelocity: Vec3.Zero,
                MissileStartingPosition: Vec3.Zero,
                VictimAgentCurVelocity: Vec3.Zero,
                GroundNormal: new Vec3(0f, 0f, 1f));

            float momentum = 1f;
            MeleeCollisionReaction colReaction = MeleeCollisionReaction.Invalid;
            HitParticleResultData particles = default(HitParticleResultData);
            float before = target.HitPoint;

            MeleeHitPatch.Invoke(mission, ref collisionData, agent, null, ref momentum, ref colReaction,
                                 blowDir, blowDir, ref particles, GameEntity.CreateFromWeakEntity(hit));

            HitHaptic((int)slot.Role, collisionData.InflictedDamage);
            if (BlockSound && !VrCollide.Collides(target))   // TEST 646: a solid gate already clanged on contact
                VrCombatSound.Clash(mission, point, VrCombatSound.Material(usage, false), "wood_shield",
                                    agent.Index, -1);

            if (Probe)
            {
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 641 object: your {0} {1} '{2}' at {3:F1} m/s for {4} damage (engine), hit points "
                    + "{5:F0} -> {6:F0} of {7:F0}{8}.",
                    usage.WeaponClass, thrust ? "stabbed" : "struck", hit.Name ?? "?", velocity.Length,
                    collisionData.InflictedDamage, before, target.HitPoint, target.MaxHitPoint,
                    target.IsDestroyed ? " - DESTROYED" : string.Empty));
            }
        }

        private struct Pending
        {
            public Agent Victim;
            public Capsule Capsule;
            public Vec3 Point;
            public Vec3 Velocity;
            public bool Shield;
            public VrBlade.Plate Plate;
        }

        private static readonly Pending[] _pending = new Pending[8];

        /// <summary>
        /// TEST 316: SweepContact for a shield plate - the earliest of the tick's
        /// blade poses, moved into the holder's frame, within `pad` of the plate's
        /// faces. `along` is where on the blade (0 grip, 1 tip).
        /// </summary>
        private static bool SweepPlate(Vec3 prevGrip, Vec3 prevTip, Vec3 grip, Vec3 tip, int steps, Vec3 shift,
                                       VrBlade.Plate plate, float pad, out float t, out float along)
        {
            t = 0f;
            along = 0f;

            int poses = steps + 2;
            for (int i = 0; i < poses; i++)
            {
                float f = i / (float)(poses - 1);
                Vec3 moveBack = shift * (1f - f);
                Vec3 g = prevGrip + (grip - prevGrip) * f + moveBack;
                Vec3 e = SweepTip(prevGrip, prevTip, grip, tip, f) + moveBack;

                if (VrCollide.PlateTouch(g, e, plate, pad, out float a))
                {
                    t = f;
                    along = a;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// TEST 303: the earliest of a tick's blade poses - oldest first - that
        /// touches one capsule. t = 0 is the previous tick's pose, 1 this tick's,
        /// the in-betweens are SweepTip's. Each pose is moved by the body's travel
        /// over its age (`shift` is the body's whole-tick travel), so the test
        /// runs in the body's frame. `along` is where on the blade (0 grip, 1 tip);
        /// `gap` is the closest the blade's axis came to the capsule's. Pure, for
        /// the tests.
        /// </summary>
        private static bool SweepContact(Vec3 prevGrip, Vec3 prevTip, Vec3 grip, Vec3 tip,
                                         int steps, Vec3 shift, Vec3 capA, Vec3 capB, float limit,
                                         out float t, out float along, out float gap)
        {
            t = 0f;
            along = 0f;
            gap = float.MaxValue;

            int poses = steps + 2;
            for (int i = 0; i < poses; i++)
            {
                float f = i / (float)(poses - 1);
                Vec3 moveBack = shift * (1f - f);
                Vec3 g = prevGrip + (grip - prevGrip) * f + moveBack;
                Vec3 e = SweepTip(prevGrip, prevTip, grip, tip, f) + moveBack;

                float d = SegmentDistance(g, e, capA, capB, out float a, out float _);
                if (d < gap)
                    gap = d;

                if (d <= limit)
                {
                    t = f;
                    along = a;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// TEST 295: how many in-between poses a frame's sweep needs so no two
        /// neighbours sit more than 0.1 m apart at the tip in the victim's
        /// frame - the tip's own travel plus a galloping body's. 0.1 m is well
        /// inside a limb's capsule plus the ray's thickness. Never fewer than
        /// combat_sweep_steps; at most 8, a hitch's worth. Pure, for the tests.
        /// </summary>
        private static int SweepCuts(int minimum, float tipTravel, float dt)
        {
            int need = (int)Math.Ceiling((tipTravel + GallopSpeed * dt) / 0.1f) - 1;
            return Math.Max(minimum, Math.Min(8, need));
        }

        /// <summary>The world velocity that carries an agent: a rider moves with
        /// the horse, the same substitution the engine's own speed bonus makes
        /// (MissionCombatMechanicsHelper.ComputeRelativeSpeedDiffOfAgents).</summary>
        internal static Vec3 Carried(Agent agent)
        {
            try
            {
                return (agent.MountAgent ?? agent).Velocity;
            }
            catch (Exception)
            {
                return Vec3.Zero;
            }
        }

        /// <summary>
        /// The blade at a fraction of the way through a sweep (TEST 261). The
        /// grip lerps, and the DIRECTION is interpolated between the two poses
        /// and re-extended to the blade's own length - so an in-between ray
        /// covers every radius the blade reaches, at the angle the blade passed
        /// through. A plain lerp of the two tips would collapse the radius at
        /// the midpoint (a 90-degree swing's midpoint lands at 0.64 of the
        /// blade, not 1.0) and fall short of a body the blade actually crossed.
        /// Pure, so the test suite checks the geometry without a mission.
        /// </summary>
        internal static Vec3 SweepTip(Vec3 prevGrip, Vec3 prevTip, Vec3 grip, Vec3 tip, float t)
        {
            Vec3 prevDir = prevTip - prevGrip;
            Vec3 nowDir = tip - grip;
            Vec3 dir = prevDir * (1f - t) + nowDir * t;

            float length = dir.Length;
            if (length < 1e-4f)
                return tip;

            float reach = (prevDir.Length + nowDir.Length) * 0.5f;
            return prevGrip + (grip - prevGrip) * t + dir * (reach / length);
        }

        // --- TEST 303: bodies, from their own skeletons ---------------------

        internal struct Capsule
        {
            public Vec3 A;
            public Vec3 B;
            public float Radius;
            public sbyte Bone;
            public BoneBodyPartType Part;
        }

        internal sealed class Body
        {
            public int Agent = -1;
            public float Stamp = -1f;
            public int Count;
            public readonly Capsule[] Capsules = new Capsule[MaxBones + 1];
        }

        /// <summary>Skeleton.MaxBoneCount.</summary>
        private const int MaxBones = 64;

        /// <summary>A horse's bones carry no body part; it is one thick body.</summary>
        private const float MountRadius = 0.16f;

        private static readonly Body[] _bodies =
        {
            new Body(), new Body(), new Body(), new Body(),
            new Body(), new Body(), new Body(), new Body(),
            new Body(), new Body(), new Body(), new Body(),
            new Body(), new Body(), new Body(), new Body(),
        };

        private static int _nextBody;
        private static readonly Vec3[] _bonePos = new Vec3[MaxBones];

        /// <summary>
        /// One body as capsules, read once per tick and shared by both hands and
        /// every pose: one capsule per bone, from its parent's joint to its own,
        /// carrying the PARENT's body part (the upper arm runs from the shoulder
        /// joint to the elbow) - so the bone and the part a blow reports are the
        /// engine's own and head, neck and limb multipliers apply, where the
        /// brush always said Chest. The skull sits above the head joint, so the
        /// head gets a sphere there as well. Every index is bounded by
        /// GetBoneCount: these accessors are unbounded inside the engine
        /// (2026-09-29, Native+0x6FCC23).
        ///
        /// ponytail: joint capsules with one radius per body part.
        /// Skeleton.GetBoneBody(bone, ref CapsuleData) is the engine's own
        /// collision capsule (decompiled; nothing in vanilla's managed code calls
        /// it, so its space is unverified) - the upgrade if these prove coarse.
        /// About three native calls per bone per body per tick; cache parents and
        /// parts per skeleton if that ever shows in a profile.
        /// </summary>
        internal static Body BodyOf(Agent agent)
        {
            // TEST 316: keyed on the mission clock, so VrCollide (in the hand
            // drive) and the hit test (after it) share one fill per tick.
            float stamp = Mission.Current != null ? Mission.Current.CurrentTime : 0f;

            for (int i = 0; i < _bodies.Length; i++)
            {
                if (_bodies[i].Agent == agent.Index && _bodies[i].Stamp == stamp)
                    return _bodies[i];
            }

            Body body = _bodies[_nextBody];
            _nextBody = (_nextBody + 1) % _bodies.Length;

            body.Agent = agent.Index;
            body.Stamp = stamp;
            body.Count = 0;

            try
            {
                Fill(body, agent);
            }
            catch (Exception)
            {
                body.Count = 0;
            }

            if (body.Count > 0)
                return body;

            body.Agent = -1;
            return null;
        }

        private static void Fill(Body body, Agent agent)
        {
            MBAgentVisuals visuals = agent.AgentVisuals;
            Skeleton skeleton = visuals != null ? visuals.GetSkeleton() : null;
            GameEntity entity = visuals != null ? visuals.GetEntity() : null;
            if (skeleton == null || entity == null)
                return;

            sbyte bones = skeleton.GetBoneCount();
            if (bones < 2 || bones > MaxBones)
                return;

            MatrixFrame global = entity.GetGlobalFrame();
            for (sbyte b = 0; b < bones; b++)
                _bonePos[b] = global.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(b).origin);

            bool mount = agent.IsMount;
            sbyte pelvis = agent.Monster != null ? agent.Monster.PelvisBoneIndex : (sbyte)(-1);

            for (sbyte b = 1; b < bones; b++)
            {
                sbyte parent = skeleton.GetParentBoneIndex(b);
                if (parent < 0 || parent >= bones)
                    continue;

                BoneBodyPartType part = visuals.GetBoneTypeData(parent).BodyPartType;
                if (part == BoneBodyPartType.None)
                {
                    // A horse is all body; a human's unlabelled pelvis is the hips.
                    if (mount)
                        part = BoneBodyPartType.Chest;
                    else if (parent == pelvis)
                        part = BoneBodyPartType.Abdomen;
                    else
                        continue;
                }

                Add(body, _bonePos[parent], _bonePos[b], mount ? MountRadius : PartRadius(part),
                    parent, part);
            }

            sbyte head = agent.Monster != null ? agent.Monster.HeadLookDirectionBoneIndex : (sbyte)(-1);
            if (!mount && head >= 0 && head < bones)
            {
                Vec3 skull = _bonePos[head] + new Vec3(0f, 0f, 0.10f);
                Add(body, skull, skull, 0.11f, head, BoneBodyPartType.Head);
            }
        }

        private static void Add(Body body, Vec3 a, Vec3 b, float radius, sbyte bone,
                                BoneBodyPartType part)
        {
            if (body.Count >= body.Capsules.Length)
                return;

            body.Capsules[body.Count++] = new Capsule
            {
                A = a,
                B = b,
                Radius = radius,
                Bone = bone,
                Part = part,
            };
        }

        /// <summary>How thick each part of a body is, in metres, around the
        /// bone line. TEST 303c tightened them ("it hits when the blade is not
        /// touching the body"): the spine bones run along the BACK, so a chest
        /// radius already reaches past the back as far as the front.</summary>
        private static float PartRadius(BoneBodyPartType part)
        {
            switch (part)
            {
                case BoneBodyPartType.Head: return 0.10f;
                case BoneBodyPartType.Neck: return 0.06f;
                case BoneBodyPartType.Chest: return 0.15f;
                case BoneBodyPartType.Abdomen: return 0.14f;
                case BoneBodyPartType.ShoulderLeft:
                case BoneBodyPartType.ShoulderRight: return 0.07f;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: return 0.05f;
                default: return 0.07f;
            }
        }

        /// <summary>
        /// TEST 306: which blow the weapon can actually deliver. Most spears,
        /// every pike and every javelin are THRUST-ONLY in the game's data
        /// (crafting_pieces.xml: no Swing entry, so SwingDamageFactor 0 and
        /// SwingDamageType Invalid), and a swing on one is computed as zero
        /// damage while the hit sound still plays - the "only a hit sound on
        /// polearms" report, and the lance's 0/ShrugOff/Bounced pattern in the
        /// journal. A weapon that can only thrust thrusts, one that can only
        /// swing swings, and only one that can do both is read from the motion.
        /// Pure, for the tests.
        /// </summary>
        private static bool ChooseThrust(float motionDot, int swingDamage, DamageTypes swingType,
                                         int thrustDamage, DamageTypes thrustType, float threshold)
        {
            bool canSwing = swingDamage > 0 && swingType != DamageTypes.Invalid;
            bool canThrust = thrustDamage > 0 && thrustType != DamageTypes.Invalid;

            if (canSwing != canThrust)
                return canThrust;

            return motionDot > threshold;
        }

        /// <summary>
        /// A contact became a blow: the victim's own guard and raised shield get
        /// their chance to stop it, and the blow lands through the engine's own
        /// Mission.MeleeHitCallback (TEST 263) - the mod decides only where and
        /// how things touched.
        /// </summary>
        private static void Strike(Mission mission, Agent agent, VrHandSlot slot,
                                   WeaponComponentData usage, VrBlade.Segment blade, Swing s,
                                   Vec3 velocity, Vec3 hand, Agent victim, sbyte bone,
                                   BoneBodyPartType part, Vec3 hitPoint, bool shieldHit = false,
                                   VrBlade.Plate plate = default(VrBlade.Plate))
        {
            // TEST 306: the guard and shield tests take the part of the weapon
            // that strikes - the hand to the tip. A polearm's shaft behind the
            // fist used to "block" against any raised shield near it.
            VrBlade.Segment striking = blade;
            striking.Grip = Vec3.Zero;

            // TEST 265: the victim's own guard or raised shield, from real
            // blade contact - GuardStops against their TEST-262-fitted weapon,
            // ShieldStops (unchanged) against their measured plate. They only
            // decide AttackBlockedWithShield / CorrectSideShieldBlock /
            // CollisionResult below and fall through to the SAME MeleeHitCallback
            // a clean hit takes, so a block's stagger, a shield's own durability
            // damage and the combat log are the engine's.
            bool blockedWithShield = false;
            bool correctSideShieldBlock = false;
            CombatCollisionResult collisionResult = CombatCollisionResult.StrikeAgent;

            if (shieldHit)
            {
                // TEST 316: the blade met their shield's plate - wherever it is
                // held. Blocked with the shield, through the same callback: the
                // shield's wear, the stagger and the log are the engine's.
                collisionResult = CombatCollisionResult.Blocked;
                blockedWithShield = true;
                correctSideShieldBlock = true;
                bone = plate.Bone;
                part = BoneBodyPartType.ArmLeft;

                WeaponComponentData theirShieldUsage = plate.Slot != EquipmentIndex.None
                    ? Held(victim, plate.Slot) : null;
                ClashHaptic((int)slot.Role);

                if (BlockSound)
                {
                    VrCombatSound.Clash(mission, hitPoint,
                                        VrCombatSound.Material(usage, false),
                                        VrCombatSound.Material(theirShieldUsage, true),
                                        agent.Index, victim.Index);
                }

                if (BlockProbe)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 316 shield: '{0}' [{1}] took your {2} on its {3} at {4:F1} m/s, {5:F2} m from "
                        + "the plate's centre (guard {6}).",
                        victim.Name ?? "?", victim.Index, usage.WeaponClass,
                        VrCombatSound.Material(theirShieldUsage, true), velocity.Length,
                        (hitPoint - plate.Centre).Length, SafeAction(victim)));
                }
            }
            else if (GuardStops(victim, striking, out string guardName, out float guardGap,
                          out WeaponComponentData guardUsage))
            {
                collisionResult = CombatCollisionResult.Blocked;
                ClashHaptic((int)slot.Role);

                if (BlockSound)
                {
                    VrCombatSound.Clash(mission,
                                        ClosestOnSegment(Chest(victim), striking.GripWorld,
                                                         striking.TipWorld),
                                        VrCombatSound.Material(usage, false),
                                        VrCombatSound.Material(guardUsage,
                                            guardUsage != null && guardUsage.IsShield),
                                        agent.Index, victim.Index);
                }

                if (BlockProbe)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 265 block: '{0}' [{1}] was guarding ({2}) and your {3} touched "
                        + "theirs {4:F2} m from grip to grip - the engine's own collision "
                        + "decides what happens next.",
                        victim.Name ?? "?", victim.Index, guardName, usage.WeaponClass, guardGap));
                }
            }
            else if (ShieldStops(agent, victim, hand, hitPoint, Math.Min(blade.Radius, BladeEdge),
                                 out VrBlade.Shield theirShield, out WeaponComponentData theirShieldUsage))
            {
                collisionResult = CombatCollisionResult.Blocked;
                blockedWithShield = true;
                correctSideShieldBlock = true;

                float shieldGap = PointSegment(theirShield.CentreWorld, hand, hitPoint);
                ClashHaptic((int)slot.Role);

                if (BlockSound)
                {
                    VrCombatSound.Clash(mission, theirShield.CentreWorld,
                                        VrCombatSound.Material(usage, false),
                                        VrCombatSound.Material(theirShieldUsage, true),
                                        agent.Index, victim.Index);
                }

                if (BlockProbe)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 265 shield: '{0}' [{1}] raised {2} and your {3} met the plate "
                        + "{4:F2} m from its centre - the engine's own shield damage decides "
                        + "what happens next.",
                        victim.Name ?? "?", victim.Index,
                        VrCombatSound.Material(theirShieldUsage, true), usage.WeaponClass,
                        shieldGap));
                }
            }

            // The blow is the touching point's motion AGAINST THE VICTIM (TEST
            // 295/303): the hand, the blade's turn, and what carries each side.
            // A blow with no direction is one the engine cannot react to; the
            // blade's own axis is the honest stand-in on the slow tail.
            Vec3 axis = blade.AxisWorld;
            Vec3 blowDir = velocity.LengthSquared > 0.01f ? velocity.NormalizedCopy() : axis;
            float motionDot = Vec3.DotProduct(blowDir, axis);

            // TEST 306: what the weapon can do decides first, the motion second.
            bool thrust = ChooseThrust(motionDot, usage.SwingDamage, usage.SwingDamageType,
                                       usage.ThrustDamage, usage.ThrustDamageType, ThrustDot);
            bool forced = thrust != (motionDot > ThrustDot);

            StrikeType strikeType = thrust ? StrikeType.Thrust : StrikeType.Swing;
            DamageTypes damageType = thrust ? usage.ThrustDamageType : usage.SwingDamageType;

            // FRIENDLY FIRE IS THE ONE PLACE THE OLD PATH STAYS. Vanilla's own
            // CancelsDamageAndBlocksAttackBecauseOfNonEnemyCase runs INSIDE
            // MeleeHitCallback and zeroes the damage for a friend or neutral in
            // single player; combat_friendly_fire = 1 is this mod's deliberate
            // override of exactly that rule, so a non-enemy (only ever reached
            // through Target's override) is built and registered by hand, and
            // their own guard stops that path outright.
            if (collisionResult == CombatCollisionResult.Blocked && !agent.IsEnemyOf(victim))
            {
                s.Hits++;
                return;
            }

            if (!agent.IsEnemyOf(victim))
            {
                StrikeFriendly(agent, slot, usage, s, victim, bone, hitPoint, blowDir, thrust,
                               damageType, part);
                return;
            }

            MissionWeapon weapon = agent.Equipment[slot.Equipment];
            sbyte attackBoneIndex = agent.Monster == null
                ? (sbyte)(-1)
                : (slot.Role == HandRole.Main
                    ? agent.Monster.MainHandItemBoneIndex
                    : agent.Monster.OffHandItemBoneIndex);

            // VrMelee's own gesture direction when it fired one; otherwise a
            // plausible direction for the strike type - only AttackProgress's
            // plateau selection and the AI's auto-block reading consult this.
            UsageDirection attackDirection =
                VrMelee.Enabled && VrMelee.SwingDirection != UsageDirection.None
                    ? VrMelee.SwingDirection
                    : (thrust ? UsageDirection.AttackDown : UsageDirection.AttackLeft);

            // TEST 303/306: how far along the weapon the contact sits, FROM THE
            // HAND - the field the engine's own damage maths reads (Mission-
            // CombatMechanicsHelper: (0.2 + CollisionDistanceOnWeapon) /
            // (GetRealWeaponLength() + GetCurWeaponOffset().z)). The share of the
            // hand-to-tip line we measure, put on the engine's own length. It was
            // a share measured from the mesh's near end, which on a spear is the
            // butt: a spear-head hit read as a hit near the hand.
            float handToTip = Vec3.DotProduct(blade.TipWorld - hand, axis);
            float contactFraction = handToTip > 1e-3f
                ? Clamp01(Vec3.DotProduct(hitPoint - hand, axis) / handToTip)
                : 1f;
            float weaponLength = usage.GetRealWeaponLength();
            float offsetZ;
            try { offsetZ = agent.GetCurWeaponOffset().z; } catch (Exception) { offsetZ = 0f; }
            float collisionDistanceOnWeapon = contactFraction * (weaponLength + offsetZ);

            // TEST 306: the outer half of the weapon is its point. A non-tip
            // thrust costs x0.15 in the engine (NonTipThrustHitDamageMultiplier).
            bool thrustTipHit = thrust && contactFraction >= 0.5f;

            // The AttackProgress that puts SpeedGraphFunction on its own
            // plateau - speed factor exactly 1.0 (vanilla-exact damage, not
            // scaled by our own guess at how fast the swing was).
            float attackProgress = MeleeHitPatch.ComputeAttackProgress(strikeType, attackDirection);

            // Stun periods MeleeHitCallback itself never derives (native code
            // sets them before a real callback), computed the vanilla way with
            // the same collisionResult a real Blocked collision would carry.
            MeleeHitPatch.ComputeStunPeriods(agent, victim, (int)slot.Equipment, strikeType,
                attackDirection, collisionDistanceOnWeapon, attackProgress, collisionResult,
                out float attackerStun, out float defenderStun, out bool _);

            AttackCollisionData collisionData = AttackCollisionData.GetAttackCollisionDataForDebugPurpose(
                _attackBlockedWithShield: blockedWithShield,
                _correctSideShieldBlock: correctSideShieldBlock,
                _isAlternativeAttack: false,
                _isColliderAgent: true,
                _collidedWithShieldOnBack: false,
                _isMissile: false,
                _isMissileBlockedWithWeapon: false,
                _missileHasPhysics: false,
                _entityExists: false,
                _thrustTipHit: thrustTipHit,
                _missileGoneUnderWater: false,
                _missileGoneOutOfBorder: false,
                collisionResult: collisionResult,
                affectorWeaponSlotOrMissileIndex: (int)slot.Equipment,
                StrikeType: (int)strikeType,
                DamageType: (int)damageType,
                CollisionBoneIndex: bone,
                VictimHitBodyPart: part,
                AttackBoneIndex: attackBoneIndex,
                AttackDirection: attackDirection,
                PhysicsMaterialIndex: 0,
                CollisionHitResultFlags: CombatHitResultFlags.NormalHit,
                AttackProgress: attackProgress,
                CollisionDistanceOnWeapon: collisionDistanceOnWeapon,
                AttackerStunPeriod: attackerStun,
                DefenderStunPeriod: defenderStun,
                MissileTotalDamage: 0f,
                MissileInitialSpeed: 0f,
                ChargeVelocity: 0f,
                FallSpeed: 0f,
                WeaponRotUp: new Vec3(0f, 0f, 1f),
                _weaponBlowDir: blowDir,
                CollisionGlobalPosition: hitPoint,
                MissileVelocity: Vec3.Zero,
                MissileStartingPosition: Vec3.Zero,
                VictimAgentCurVelocity: victim.Velocity,
                GroundNormal: new Vec3(0f, 0f, 1f));

            // TEST 303: every body the blade crosses gets a fresh blow - momentum
            // 1.0 each, deliberately: "no miss" over vanilla's cleave falloff.
            float momentum = 1f;
            MeleeCollisionReaction colReaction = MeleeCollisionReaction.Invalid;
            HitParticleResultData particles = default(HitParticleResultData);
            MeleeHitPatch.LastOurBlowFlags = BlowFlags.None;

            float before = victim.Health;

            MeleeHitPatch.Invoke(mission, ref collisionData, agent, victim, ref momentum,
                                 ref colReaction, blowDir, blowDir, ref particles);

            float after = victim.Health;

            // TEST 261: the hand that landed the hit says so, scaled by the
            // engine's OWN inflicted damage.
            HitHaptic((int)slot.Role, collisionData.InflictedDamage);

            // Vanilla's own particle names (DefaultDamageParticleModel), spawned
            // by hand: the native code that renders a burst from a particle
            // INDEX only runs for a call the ENGINE made.
            // ponytail: hardcoded vanilla names rather than reversing
            // particles.StartHitParticleIndex; add a real lookup if a mod ever
            // changes the damage particle model. A block reports the shield's
            // durability loss as InflictedDamage, so it is gated out here -
            // the clash above is its only feedback.
            if (collisionResult != CombatCollisionResult.Blocked
                && (collisionData.InflictedDamage > 0 || after <= 0f))
            {
                try
                {
                    bool noBlood = (usage.WeaponFlags & WeaponFlags.NoBlood) != 0;
                    var pframe = MatrixFrame.Identity;
                    pframe.origin = hitPoint;
                    mission.AddParticleSystemBurstByName(
                        noBlood ? "psys_game_sweat_sword_enter" : "psys_game_blood_sword_enter",
                        pframe, false);
                }
                catch (Exception)
                {
                    // A missing burst is a cosmetic loss, not a lost hit.
                }
            }

            s.Hits++;

            // TEST 317: a blow that landed lets the edge bite into the body.
            if (collisionResult != CombatCollisionResult.Blocked)
                VrCollide.Struck(slot.Role, victim, damageType);

            // A lance's own "couch" usage (item_usage_sets.xml: requires_mount +
            // passive_usage) zeroes ComputeBlowMagnitudeMelee's damage outright
            // whenever agent.IsDoingPassiveAttack is true and NEITHER side is
            // mounted - read live off the attacker, so a 0 is only explained
            // here, not caused here.
            bool passiveAttack;
            try { passiveAttack = agent.IsDoingPassiveAttack; }
            catch (Exception) { passiveAttack = false; }

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 303 hit: '{0}' [{1}] {2} (bone {3}). '{4}' ({5}, usage '{6}' #{7}) {8}{9} "
                + "at {10:F1} m/s, {11:0}% from the hand to the tip - game damage {12} (armor "
                + "absorbed {13}), flags {14}, reaction {15}. Health {16:F1} -> {17:F1}.{18}{19}",
                victim.Name ?? "?", victim.Index, PartName(part), bone,
                weapon.Item?.StringId ?? "?", usage.WeaponClass, usage.ItemUsage ?? "?",
                weapon.CurrentUsageIndex,
                thrust ? "thrust" : "swing",
                forced ? (thrust ? " (it cannot swing)" : " (it cannot thrust)") : string.Empty,
                velocity.Length, contactFraction * 100f,
                collisionData.InflictedDamage, collisionData.AbsorbedByArmor,
                MeleeHitPatch.LastOurBlowFlags, colReaction, before, after,
                after < 1f ? " THE ENGINE'S Die() RAN." : string.Empty,
                passiveAttack && !agent.HasMount
                    ? " PASSIVE ATTACK, NOT MOUNTED: vanilla's own couched-weapon rule zeroes "
                      + "this hit's damage - not this mod's doing."
                    : string.Empty));
        }

        /// <summary>
        /// The hand-built path, kept only for a friend or neutral under
        /// combat_friendly_fire = 1 - see where Strike calls it. Flat weapon
        /// damage: vanilla itself refuses to compute armor or perks for a
        /// non-enemy in single player, so there is nothing real to route through.
        /// </summary>
        private static void StrikeFriendly(Agent agent, VrHandSlot slot, WeaponComponentData usage,
                                           Swing s, Agent victim, sbyte bone, Vec3 hitPoint,
                                           Vec3 blowDir, bool thrust, DamageTypes damageType,
                                           BoneBodyPartType part)
        {
            int damage = Math.Max(1, thrust ? usage.ThrustDamage : usage.SwingDamage);
            MissionWeapon weapon = agent.Equipment[slot.Equipment];

            var blow = new Blow(agent.Index);
            blow.WeaponRecord.FillAsMeleeBlow(weapon.Item, usage, (int)slot.Equipment,
                                              agent.Monster == null
                                                  ? (sbyte)(-1)
                                                  : (slot.Role == HandRole.Main
                                                      ? agent.Monster.MainHandItemBoneIndex
                                                      : agent.Monster.OffHandItemBoneIndex));
            blow.GlobalPosition = hitPoint;
            blow.Direction = blowDir;
            blow.SwingDirection = blowDir;
            blow.StrikeType = thrust ? StrikeType.Thrust : StrikeType.Swing;
            blow.AttackType = AgentAttackType.Standard;
            blow.DamageType = damageType;
            blow.BoneIndex = bone;
            blow.VictimBodyPart = part;
            blow.InflictedDamage = damage;
            blow.BaseMagnitude = damage;
            blow.MovementSpeedDamageModifier = 1f;
            blow.BlowFlag = BlowFlags.None;

            float before = victim.Health;
            victim.RegisterBlow(blow, default(AttackCollisionData));
            float after = victim.Health;

            HitHaptic((int)slot.Role, damage);
            s.Hits++;

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 303 friendly-fire hit: '{0}' [{1}] {2} (bone {3}) - combat_friendly_fire is "
                + "on, so this is the hand-built path (vanilla's own MeleeHitCallback zeroes "
                + "non-enemy damage in single player): flat {4} {5} damage. Health {6:F1} -> "
                + "{7:F1}.",
                victim.Name ?? "?", victim.Index, PartName(part), bone, damage, damageType,
                before, after));
        }

        /// <summary>
        /// Whether a physical strike may land on this agent (TEST 242, TEST 261).
        /// The player's own mount never can; every other active body can, except
        /// friends and neutrals when friendly fire is off. That matches vanilla,
        /// which cancels melee damage on friends and neutrals in single player
        /// itself: Mission.CancelsDamageAndBlocksAttackBecauseOfNonEnemyCase.
        ///
        /// Friendly fire is off by default (combat_friendly_fire, or the VR
        /// panel's checkbox), and a mission that says no - ForceNoFriendlyFire,
        /// IsFriendlyMission - refuses it even when the setting is on.
        /// </summary>
        internal static bool Target(Agent attacker, Agent hit)
        {
            if (hit == null || hit == attacker || !hit.IsActive())
                return false;

            if (!hit.IsHuman && !hit.IsMount)
                return false;

            if (attacker.MountAgent == hit)
                return false;

            if (hit.IsMount && !HitMounts)
                return false;

            if (_friendlyFire && !MissionForbidsFriendlyFire())
                return true;

            return attacker.IsEnemyOf(hit);
        }

        /// <summary>The game's own flags that forbid friend-on-friend damage:
        /// ForceNoFriendlyFire is set on missions that must not allow it, and
        /// IsFriendlyMission marks the peaceful ones.</summary>
        private static bool MissionForbidsFriendlyFire()
        {
            Mission mission = Mission.Current;
            return mission != null && (mission.ForceNoFriendlyFire || mission.IsFriendlyMission);
        }

        /// <summary>The defender's guard as the log names it.</summary>
        private static string SafeAction(Agent agent)
        {
            try { return agent.GetCurrentActionType(1).ToString(); }
            catch (Exception) { return "?"; }
        }

        /// <summary>The enum's aliases (CriticalBodyPartsBegin is Head) lose to
        /// Mono's name lookup in a ToString, so the log says Head.</summary>
        private static string PartName(BoneBodyPartType part)
        {
            return part == BoneBodyPartType.Head ? "Head" : part.ToString();
        }

        // --- blocking (TEST 238) -------------------------------------------

        /// <summary>
        /// Called from the RegisterBlow prefixes for every blow in the mission.
        /// True lets the blow through; false cancels it.
        ///
        /// THE TEST: THE ATTACKER'S BLADE, FROM THE ENGINE'S OWN COLLISION
        ///
        /// TEST 238-239 built the attacker's weapon out of the attach bone and
        /// the impact point, because the equipment entity proved (TEST 208) to
        /// be parked at the agent's feet. TEST 242 adds the engine's own
        /// collision data (AttackCollisionData), which is only available at
        /// Mission.RegisterBlow: the contact point, how far ALONG the attacker's
        /// weapon it sits (CollisionDistanceOnWeapon) and the directions the
        /// weapon was moving (WeaponBlowDir, WeaponRotUp, the last bone
        /// segment). The blade is placed on the line through the contact in the
        /// direction that best explains the along-distance and the grip; the
        /// attach-bone chord stays as the fallback, and blows with no collision
        /// data (the Agent.RegisterBlow fallback path) behave exactly as they
        /// did in TEST 239e.
        ///
        /// The player's blade is tested as its pose NOW plus the last few poses
        /// (combat_block_sweep_ms), so a guard that met the weapon between two
        /// samples still blocks.
        ///
        /// The shield test (TEST 241) is the same line against the plate's
        /// centre, and only while the shield is raised to chest level - see
        /// ShieldUp. A hand with a shield has no blade, and vice versa.
        ///
        /// The player's own body is checked, and the horse under the player:
        /// a blow that lands on your mount is a blow on you as far as the guard
        /// is concerned. A blow on anyone else returns immediately.
        /// </summary>
        internal static bool BeforePlayerBlow(Agent victim, Blow blow)
        {
            if (!Enabled || _broken || !Block || !VrSystem.IsActive)
                return true;

            Mission mission = Mission.Current;
            Agent attacker = mission != null ? mission.FindAgentWithIndex(blow.OwnerId) : null;

            return BeforeBlow(mission, attacker, victim, blow, default(AttackCollisionData));
        }

        /// <summary>TEST 242: the same decision at Mission.RegisterBlow, which
        /// carries the collision data AND runs before the combat log - so a
        /// blocked blow never queues the damage number the player used to see.</summary>
        internal static bool BeforeMeleeBlow(Agent attacker, Agent victim, Blow blow,
                                             in AttackCollisionData collisionData)
        {
            if (!Enabled || _broken || !Block || !VrSystem.IsActive)
                return true;

            return BeforeBlow(Mission.Current, attacker, victim, blow, collisionData);
        }

        private static bool BeforeBlow(Mission mission, Agent attacker, Agent victim, Blow blow,
                                       AttackCollisionData collisionData)
        {
            // TEST 264: while motion melee is on, the PRIMARY guard is the
            // engine's own block STATE - a Defend bit VrMelee writes the
            // instant your blade touches the attacker's (TryWeaponBladeContact)
            // - and it resolves most blows natively before this ever runs:
            // this method only ever reaches a blow the engine already decided
            // was NOT blocked, with nonzero damage (the check a few lines
            // below), so it can only catch what the native resolution missed
            // (the Defend bit landing a tick or two late), never double up on
            // what it caught. TEST 245 skipped this test outright whenever
            // VrMelee was on; that was only correct while the guard's trigger
            // was a static hand pose with no notion of "just touched" at all.

            try
            {
                if (mission == null || victim == null)
                    return true;

                // The player's body, or the horse the player is riding.
                Agent main = mission.MainAgent;
                bool playerBody = main != null
                    && (victim == main || (victim.IsMount && victim.RiderAgent == main));
                if (!playerBody)
                    return true;

                if (blow.InflictedDamage <= 0 || blow.OwnerId == -1 || blow.OwnerId == victim.Index)
                    return true;

                // A horse charge cannot be stopped by a blade or a shield
                // (TEST 243). Kicks and bashes fall through to the guard test
                // below: a raised shield is where they should stop, and with no
                // weapon line for them the shield's chest test decides.
                if (blow.AttackType == AgentAttackType.Collision)
                    return true;

                // Arrows and bolts stop on a raised shield and on nothing else
                // (TEST 243), which is the vanilla rule: no shield, no missile
                // block. The engine resolves missile collisions against the
                // defender's block STATE, and the player never plays one, so a
                // VR shield would otherwise be transparent to every arrow.
                if (blow.IsMissile)
                {
                    if (!BlockArrows)
                        return true;

                    return !MissileGuard(mission, attacker, main, victim, blow, collisionData);
                }

                // TEST 242: any attacker, not only enemies, so a stray friendly
                // blow can be blocked too.
                if (attacker == null || attacker == victim || !attacker.IsActive())
                    return true;

                // TEST 311: your whole blade touched this attacker's whole blade
                // a moment ago. The engine's guard was raised for it and landed
                // late; the touch is the block, wherever along the blade it was.
                long sinceContact = Stopwatch.GetTimestamp() - _contactAt;
                if (attacker.Index == _contactIndex && _contactAt != 0L
                    && sinceContact <= Seconds(BlockLatchMs * 0.001f))
                {
                    _contactIndex = -1;
                    Guard(mission, victim, attacker, blow, (int)HandRole.Main, "blade",
                          string.Format(CultureInfo.InvariantCulture,
                              "their blade, touched {0:0} ms before impact",
                              sinceContact * 1000.0 / Stopwatch.Frequency),
                          _contactGap, _contactLimit, 0f, -1f, "TEST 311 latch");
                    return false;
                }

                // The attacker's weapon: the bone the engine attached it to (its
                // own answer, carried on the blow) and the point its collision
                // says the weapon touched. AffectorWeaponSlotOrMissileIndex is
                // the engine's answer to "what hit me", not a guess. See
                // AttachBone and TryBoneWorld.
                EquipmentIndex theirSlot =
                    (EquipmentIndex)blow.WeaponRecord.AffectorWeaponSlotOrMissileIndex;

                sbyte theirBone = theirSlot != EquipmentIndex.None
                    ? AttachBone(attacker, theirSlot, blow)
                    : (sbyte)(-1);

                MatrixFrame theirFrame = MatrixFrame.Identity;
                bool haveTheirLine = theirBone >= 0
                                     && VrBlade.TryBoneWorld(attacker, theirBone, out theirFrame);

                Vec3 theirGrip = haveTheirLine ? theirFrame.origin : Vec3.Zero;
                float theirRadius = BlockTheirRadius;
                float theirLength = WeaponLength(attacker, theirSlot);
                Vec3 contact = blow.GlobalPosition;

                // THE BLADE'S OWN DIRECTION, FROM THE ENGINE (TEST 242).
                //
                // CollisionDistanceOnWeapon is how far along the attacker's
                // weapon the contact sits - the engine's own answer, and the one
                // its damage maths uses. Given that and the contact, a candidate
                // direction is right when the contact projects onto it at
                // `along` and the grip (at the attach bone) lies on it. The
                // attach-bone chord is the fallback; a candidate only replaces
                // it when it explains the along-distance better. Each direction
                // is logged, so one session says which one the engine means.
                float along = collisionData.CollisionDistanceOnWeapon;
                Vec3 axis = Vec3.Zero;
                bool haveAxis = false;
                string axisName = "chord";

                if (haveTheirLine)
                {
                    Vec3 toContact = contact - theirGrip;
                    float chord = toContact.Length;

                    if (chord > 1e-4f)
                    {
                        axis = toContact * (1f / chord);
                        haveAxis = true;
                    }

                    if (along > 1e-3f && along <= theirLength + 0.05f)
                    {
                        Vec3 best = axis;
                        string bestName = "chord";
                        float candidateScore = haveAxis
                            ? LineScore(theirGrip, contact, axis, along)
                            : float.MaxValue;

                        TryCandidate(ref best, ref candidateScore, ref bestName, "blow",
                                     collisionData.WeaponBlowDir, theirGrip, contact, along);
                        TryCandidate(ref best, ref candidateScore, ref bestName, "weapon axis",
                                     Vec3.CrossProduct(collisionData.WeaponRotUp,
                                                       collisionData.WeaponBlowDir),
                                     theirGrip, contact, along);
                        TryCandidate(ref best, ref candidateScore, ref bestName, "last segment",
                                     collisionData.LastBoneSegmentSwingDir, theirGrip, contact, along);
                        TryCandidate(ref best, ref candidateScore, ref bestName, "last axis",
                                     Vec3.CrossProduct(collisionData.LastBoneSegmentRotUp,
                                                       collisionData.LastBoneSegmentSwingDir),
                                     theirGrip, contact, along);

                        if (bestName != "chord" && candidateScore <= LineSlack)
                        {
                            axis = best;
                            haveAxis = true;
                            axisName = bestName;
                        }
                    }
                }

                // THE WHOLE BLADE (TEST 239c). The hand-to-impact chord is only
                // the weapon's span at the contact frame; it says nothing about
                // the rest of the blade, which is why blocking felt tied to the
                // hand: a guard that met the enemy's blade away from that short
                // line was scored as out of reach. The item's own AABB - the
                // same read the player's blade gets - composed with the attach
                // bone reconstructs the whole weapon. It is used only when the
                // engine's own contact lies within a blade's width of it, which
                // is what separates the drawn weapon from the batched mesh TEST
                // 238e tripped over.
                //
                // TEST 242: the shape now comes from the ITEM's own mesh, not
                // the agent's batched equipment entity - the same shield
                // measured 1.21 m on one spawn and 0.35 m on the next. See
                // VrBlade.ItemMeshes.
                //
                // There is deliberately NO "swing heading toward your chest"
                // line any more: TEST 239d added one and a sword simply raised
                // in front of the torso blocked everything, because the heading
                // passes near any guard. Blocking is two weapons meeting - the
                // blade has to be brought to the attacker's weapon, with
                // combat_block_slack as the only forgiveness.
                VrBlade.Segment theirFull = default(VrBlade.Segment);
                bool haveFull = false;
                float fullGap = -1f;

                if (haveTheirLine && theirSlot != EquipmentIndex.None
                    && VrBlade.TryGetWeaponShape(attacker, theirSlot, out Vec3 fullGrip,
                                                 out Vec3 fullTip, out float fullRadius))
                {
                    theirFull.Valid = true;
                    theirFull.World = theirFrame;
                    theirFull.Grip = fullGrip;
                    theirFull.Tip = fullTip;
                    theirFull.Radius = fullRadius;

                    fullGap = PointSegment(contact, theirFull.GripWorld, theirFull.TipWorld);
                    haveFull = fullGap <= fullRadius + 0.10f;
                }

                var lineA = new Vec3[2];
                var lineB = new Vec3[2];
                var lineName = new string[2];
                var lineRadius = new float[2];
                int lines = 0;

                if (haveFull)
                {
                    lineA[lines] = theirFull.GripWorld;
                    lineB[lines] = theirFull.TipWorld;
                    lineName[lines] = "their blade";
                    lineRadius[lines] = theirFull.Radius;
                    lines++;
                }

                if (haveAxis && along > 1e-3f && along <= theirLength + 0.05f)
                {
                    // The contact sits `along` metres from the grip; the blade runs
                    // the item's real length through it, in the direction the
                    // collision data chose. The engine's own collision, extended
                    // over the whole weapon.
                    float before = Math.Max(0f, Math.Min(theirLength, along));
                    float after = Math.Max(0f, theirLength - before);

                    lineA[lines] = contact - axis * before;
                    lineB[lines] = contact + axis * after;
                    lineName[lines] = "their weapon";
                    lineRadius[lines] = haveFull ? theirFull.Radius : theirRadius;
                    lines++;
                }
                else if (haveTheirLine)
                {
                    Vec3 toImpact = contact - theirGrip;
                    float chord = toImpact.Length;

                    if (chord > 1e-4f)
                    {
                        Vec3 dir = toImpact * (1f / chord);
                        float extra = Math.Min(1.2f, Math.Max(0f, theirLength - chord));

                        lineA[lines] = theirGrip;
                        lineB[lines] = contact + dir * extra;
                        lineName[lines] = "their weapon (chord)";
                        lineRadius[lines] = theirRadius;
                        lines++;
                    }
                }

                // The old test's number, kept for the probe: how far the parked
                // slot entity is from the attach bone. This is the whole TEST
                // 238 diagnosis in one figure and it should be large.
                float parked = -1f;
                if (BlockProbe && theirSlot != EquipmentIndex.None && haveTheirLine)
                {
                    try
                    {
                        WeakGameEntity parkedEntity =
                            attacker.GetWeaponEntityFromEquipmentSlot(theirSlot);

                        if (parkedEntity.IsValid)
                            parked = (parkedEntity.GetGlobalFrame().origin - theirGrip).Length;
                    }
                    catch (Exception)
                    {
                        // A probe that cannot read is a probe that says nothing.
                    }
                }

                string bestHand = null;
                string bestTest = null;
                float bestDistance = float.MaxValue;
                float bestLimit = 0f;
                float bestGlide = 0f;
                float bestAlong = -1f;
                float bestScore = float.MaxValue;

                for (int role = 0; role < Roles; role++)
                {
                    VrBlade.Shield shield = _shield[role];
                    bool haveShield = shield.Valid
                        && ShieldUp(main, shield.CentreWorld, attacker.GetEyeGlobalPosition());

                    // The blade poses worth testing: the one this tick's swing
                    // pass left, then the sweep ring's fresh ones (TEST 242).
                    long now = Stopwatch.GetTimestamp();
                    bool haveBlade = _live[role].Valid || SweepHasAny(role, now);

                    if (!haveBlade && !haveShield)
                        continue;

                    // 1. THE BLADE against every attacker line that exists, and
                    //    against every recent pose of it.
                    if (haveBlade)
                    {
                        float distance = float.MaxValue;
                        float limit = 1f;
                        string from = null;
                        float alongHere = -1f;
                        float glide = 0f;
                        bool found = false;

                        for (int pose = -1; pose < SweepSlots; pose++)
                        {
                            VrBlade.Segment blade;
                            if (pose < 0)
                            {
                                blade = _live[role];
                            }
                            else
                            {
                                if (!SweepFresh(role, pose, now))
                                    continue;

                                blade = _sweep[role].Segments[pose];
                            }

                            if (!blade.Valid)
                                continue;

                            // A blow running along the blade glides off it (TEST
                            // 238d: off by default - the geometry is the test).
                            float segmentGlide = Math.Abs(
                                Vec3.DotProduct(blow.SwingDirection, blade.AxisWorld));

                            for (int i = 0; i < lines; i++)
                            {
                                float d = SegmentDistance(blade.GripWorld, blade.TipWorld,
                                                          lineA[i], lineB[i],
                                                          out float t, out float _);
                                float l = blade.Radius + lineRadius[i] + BlockSlack;

                                if (!found || d / l < distance / limit)
                                {
                                    distance = d;
                                    limit = l;
                                    from = lineName[i];
                                    alongHere = t;
                                    glide = segmentGlide;
                                    found = true;
                                }
                            }

                            if (lines == 0)
                            {
                                float d = PointSegment(contact, blade.GripWorld, blade.TipWorld);
                                float l = BlockRadius + blade.Radius;

                                if (!found || d / l < distance / limit)
                                {
                                    distance = d;
                                    limit = l;
                                    from = "the impact";
                                    alongHere = SegmentPlace(blade, contact);
                                    glide = segmentGlide;
                                    found = true;
                                }
                            }
                        }

                        if (distance / limit < bestScore)
                        {
                            bestScore = distance / limit;
                            bestTest = from;
                            bestDistance = distance;
                            bestLimit = limit;
                            bestGlide = glide;
                            bestHand = RoleName[role];
                            bestAlong = alongHere;
                        }

                        if (distance <= limit && glide <= BlockGlide)
                        {
                            Guard(mission, victim, attacker, blow, role, "blade", from,
                                  distance, limit, glide, alongHere);
                            return false;
                        }
                    }

                    // 2. THE SHIELD, and only while it is raised to chest level
                    //    (TEST 241), against the same lines.
                    if (haveShield)
                    {
                        float distance = float.MaxValue;
                        float limit = 1f;
                        string from = null;

                        for (int i = 0; i < lines; i++)
                        {
                            float d = PointSegment(shield.CentreWorld, lineA[i], lineB[i]);
                            float l = shield.Radius + lineRadius[i] + ShieldSlack;

                            if (from == null || d / l < distance / limit)
                            {
                                distance = d;
                                limit = l;
                                from = lineName[i];
                            }
                        }

                        if (from == null)
                        {
                            distance = (shield.CentreWorld - blow.GlobalPosition).Length;
                            limit = shield.Radius + BlockRadius;
                            from = "the impact";
                        }

                        if (distance / limit < bestScore)
                        {
                            bestScore = distance / limit;
                            bestTest = from;
                            bestDistance = distance;
                            bestLimit = limit;
                            bestGlide = 0f;
                            bestHand = RoleName[role] + " shield";
                            bestAlong = -1f;
                        }

                        if (distance <= limit)
                        {
                            Guard(mission, victim, attacker, blow, role, "shield", from,
                                  distance, limit, 0f, -1f);
                            return false;
                        }
                    }
                }

                // Nothing stopped it. Say why, with the geometry, so the next
                // session tunes from numbers (TEST 238b / 239 / 241).
                if (BlockProbe)
                {
                    string where = haveTheirLine
                        ? string.Format(CultureInfo.InvariantCulture,
                            " The line is the attacker's weapon: attach bone {0}, {1:F2} m to "
                            + "the impact, {2:F2} m along it (direction: {3}){4}.",
                            theirBone, (theirGrip - contact).Length, along, axisName,
                            parked >= 0f
                                ? string.Format(CultureInfo.InvariantCulture,
                                    "; the parked slot entity is {0:F2} m from that bone", parked)
                                : string.Empty)
                        : " The engine reported no attach bone for this blow, so only the "
                          + "impact point could be tested.";

                    if (lines == 0)
                    {
                        where += " No attacker weapon line could be built, so only the "
                               + "impact point was tested.";
                    }
                    else if (haveFull)
                    {
                        where += string.Format(CultureInfo.InvariantCulture,
                            " Attacker lines: whole blade (contact gap {0:F2} m) and weapon.",
                            fullGap);
                    }
                    else
                    {
                        where += " Attacker line: the weapon from the collision (contact, "
                               + "along-distance, direction); its whole-blade reconstruction "
                               + "did not validate.";
                    }

                    string placed = bestAlong >= 0f
                        ? string.Format(CultureInfo.InvariantCulture,
                            " (at {0:0}% of your blade from the grip)", bestAlong * 100f)
                        : string.Empty;

                    if (bestHand == null)
                    {
                        VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "TEST 242 pass: '{0}' landed {1} {2} - no melee blade or raised "
                            + "shield was being measured.{3}",
                            attacker.Name ?? "?", blow.InflictedDamage, blow.DamageType, where));
                    }
                    else if (bestGlide > BlockGlide && bestTest != null
                             && bestTest.StartsWith("their weapon", StringComparison.Ordinal))
                    {
                        VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "TEST 242 pass: '{0}' landed {1} {2} - your {3} was {4} but the "
                            + "blow ran along it (glide {5:F2} vs limit {6:F2}).{7}",
                            attacker.Name ?? "?", blow.InflictedDamage, blow.DamageType,
                            bestHand, ContactText(bestTest, bestDistance, bestLimit),
                            bestGlide, BlockGlide, where));
                    }
                    else
                    {
                        VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "TEST 242 pass: '{0}' landed {1} {2} - your {3} was {4}{5}; out of "
                            + "reach.{6}",
                            attacker.Name ?? "?", blow.InflictedDamage, blow.DamageType,
                            bestHand, ContactText(bestTest, bestDistance, bestLimit), placed,
                            where));
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Failed(ex, "TEST 242: the block decision failed");
                return true;
            }
        }

        /// <summary>
        /// A block or a parry that was accepted: the haptic, the engine's own
        /// clash event by the two materials (TEST 240), and the log line.
        /// A parry is still a blade moving against the blow; a shield block is
        /// never a parry.
        ///
        /// tag names the log family: TEST 242 for melee, TEST 243 arrow for a
        /// missile stopped by a raised shield.
        /// </summary>
        private static void Guard(Mission mission, Agent victim, Agent attacker, Blow blow,
                                  int role, string how, string from, float distance,
                                  float limit, float glide, float along,
                                  string tag = "TEST 242")
        {
            // Remember the blow for BlockedLogPatch: if the combat log was
            // already queued (a session where MeleeBlowPatch did not apply), the
            // damage popup it builds would still show. Consumed on a match.
            _blockedAt = Stopwatch.GetTimestamp();
            _blockedDamage = blow.InflictedDamage;

            Vec3 move = _liveMove[role];
            bool opposing = move.LengthSquared > 1e-6f
                            && Vec3.DotProduct(move, blow.Direction) < 0f;
            float speed = _liveSpeed[role];
            bool parry = how == "blade" && opposing && speed >= ParrySpeed;

            Haptic(role, parry);

            if (BlockSound)
            {
                EquipmentIndex theirSlot =
                    (EquipmentIndex)blow.WeaponRecord.AffectorWeaponSlotOrMissileIndex;

                VrCombatSound.Clash(mission, blow.GlobalPosition,
                                    TheirMaterial(attacker, theirSlot),
                                    VrCombatSound.Material(how == "shield"
                                        ? _shieldUsage[role] : _liveUsage[role],
                                        how == "shield"),
                                    attacker.Index, victim.Index);
            }

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                tag + " {0}: '{1}' landed {2} {3} - your {4} {5} was {6:F2} m from {7} "
                + "(limit {8:F2}, at {9}, glide {10:F2}, your hand {11:F1} m/s{12}) - the "
                + "blow was cancelled.",
                parry ? "parry" : "block",
                attacker != null ? attacker.Name ?? "?" : "?", blow.InflictedDamage, blow.DamageType,
                RoleName[role], how, distance, from, limit,
                along >= 0f
                    ? string.Format(CultureInfo.InvariantCulture, "{0:0}% along your blade",
                                    along * 100f)
                    : "the plate",
                glide, speed, parry ? ", opposing" : string.Empty));
        }

        /// <summary>
        /// Whether a combat log entry is the blocked blow's and must be dropped
        /// (TEST 242). Only BlockedLogPatch calls this; with MeleeBlowPatch
        /// applied the blocked blow never reaches a combat log at all.
        /// </summary>
        internal static bool SuppressBlockedDamage(bool victimMine, int damage)
        {
            if (!victimMine || damage <= 0 || _blockedAt == 0L)
                return false;

            if (Stopwatch.GetTimestamp() - _blockedAt > Seconds(1f) || damage != _blockedDamage)
                return false;

            _blockedAt = 0L;
            return true;
        }

        /// <summary>The bone Mission.CreateMeleeBlow attached the blow's weapon
        /// to - the engine's own answer for THIS blow. The monster is consulted
        /// again only when an older or weaponless blow carries -1; no bone at
        /// all is the impact-point fallback, and the caller handles it.</summary>
        private static sbyte AttachBone(Agent attacker, EquipmentIndex slot, Blow blow)
        {
            if (blow.WeaponRecord.BoneNoToAttach >= 0)
                return blow.WeaponRecord.BoneNoToAttach;

            try
            {
                MissionWeapon weapon = attacker.Equipment[slot];
                if (!weapon.IsEmpty && attacker.Monster != null)
                    return attacker.Monster.GetBoneToAttachForItemFlags(weapon.Item.ItemFlags);
            }
            catch (Exception)
            {
                // Falls through to no bone, which is the impact fallback.
            }

            return -1;
        }

        // TryBoneWorld moved to VrBlade (TEST 262): the enemy-blade fit's probe
        // needs it too, and VrBlade is where TryGetShieldForAgent already
        // composes the same bone-world frame.

        /// <summary>
        /// The attacker weapon's own length in metres, from the item data
        /// (WeaponLength is centimetres - an empire sword's 108 against its
        /// measured 1.23 m). It extends the hand-to-impact chord past the
        /// contact and sizes the heading line; clamped so a bad field cannot
        /// make a twenty-metre sword.
        /// </summary>
        private static float WeaponLength(Agent attacker, EquipmentIndex slot)
        {
            try
            {
                if (slot != EquipmentIndex.None)
                {
                    WeaponComponentData usage = attacker.Equipment[slot].CurrentUsageItem;
                    if (usage != null && usage.WeaponLength > 0)
                        return Math.Max(0.4f, Math.Min(1.6f, usage.WeaponLength * 0.01f));
                }
            }
            catch (Exception)
            {
                // A length we cannot read is a one-metre weapon.
            }

            return 1f;
        }

        /// <summary>The physics material of whatever the attacker swung, as the
        /// engine's impact events spell it (TEST 240).</summary>
        private static string TheirMaterial(Agent attacker, EquipmentIndex slot)
        {
            try
            {
                if (slot != EquipmentIndex.None)
                {
                    WeaponComponentData usage = attacker.Equipment[slot].CurrentUsageItem;
                    if (usage != null)
                        return VrCombatSound.Material(usage, usage.IsShield);
                }
            }
            catch (Exception)
            {
                // A material we cannot read is a wood one.
            }

            return VrCombatSound.Material(null, false);
        }

        /// <summary>
        /// "Raised to chest level", as a rule rather than an aim (TEST 241).
        /// The shield's centre must sit in a band around the holder's chest
        /// bone, out in front of the torso, and on the side the threat is on.
        /// A shield hanging at the side, held low, or slung on the back fails
        /// one of the three, which is exactly the request: it guards only when
        /// it is actually up.
        /// </summary>
        private static bool ShieldUp(Agent holder, Vec3 centre, Vec3 threat)
        {
            if (holder == null)
                return false;

            Vec3 chest = Chest(holder);

            float height = centre.z - chest.z;
            if (height < ShieldLow || height > ShieldHigh)
                return false;

            Vec3 ahead = new Vec3(centre.x - chest.x, centre.y - chest.y, 0f);
            float reach = ahead.Length;
            if (reach < ShieldAhead || reach > ShieldReach)
                return false;

            Vec3 toward = new Vec3(threat.x - chest.x, threat.y - chest.y, 0f);
            return !(toward.LengthSquared > 1e-6f
                     && Vec3.DotProduct(ahead, toward) <= 0f);
        }

        /// <summary>
        /// Whether a victim's shield stops the player's blade (TEST 241). The
        /// engine's ray is body-only, so a shield is invisible to it; tested
        /// here, from the bone the engine attached it to, and only while the
        /// shield is raised to chest level. Returns the plate so the caller can
        /// play its material and say how close the blade came.
        ///
        /// TEST 303e: the part of the blade that went INTO the body - the hand
        /// to the contact - against the plate, with the blade's edge and a pad,
        /// no more. It was the whole hand-to-tip line with the blade's measured
        /// radius and combat_shield_slack: 93 of 122 contacts in one session were
        /// "blocked", some 0.60 m from the shield's centre - blows that went
        /// round it. combat_shield_slack stays the player's own shield's.
        /// </summary>
        private static bool ShieldStops(Agent attacker, Agent victim, Vec3 from, Vec3 to,
                                        float bladeRadius, out VrBlade.Shield shield,
                                        out WeaponComponentData shieldUsage)
        {
            shield = default(VrBlade.Shield);
            shieldUsage = null;

            try
            {
                EquipmentIndex[] slots =
                {
                    victim.GetOffhandWieldedItemIndex(),
                    victim.GetPrimaryWieldedItemIndex(),
                };

                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] == EquipmentIndex.None)
                        continue;

                    MissionWeapon weapon = victim.Equipment[slots[i]];
                    if (weapon.IsEmpty)
                        continue;

                    WeaponComponentData usage = weapon.CurrentUsageItem;
                    if (usage == null || !usage.IsShield)
                        continue;

                    if (!VrBlade.TryGetShieldForAgent(victim, slots[i], out VrBlade.Shield plate))
                        continue;

                    if (!ShieldUp(victim, plate.CentreWorld, attacker.GetEyeGlobalPosition()))
                        continue;

                    float gap = PointSegment(plate.CentreWorld, from, to);
                    if (gap > plate.Radius + bladeRadius + RayPad)
                        continue;

                    shield = plate;
                    shieldUsage = usage;
                    return true;
                }
            }
            catch (Exception)
            {
                // An unreadable shield is not a guard.
            }

            return false;
        }

        /// <summary>
        /// TEST 264: whether the player's weapon-hand blade is CURRENTLY
        /// touching a nearby enemy's own blade while that enemy is mid-swing
        /// (ReadyMelee/ReleaseMelee) - the trigger VrMelee.GuardPass uses
        /// instead of a hand-position zone to raise the weapon guard. The
        /// enemy's blade is TEST 262's fitted line (VrBlade.TryGetEnemyBlade);
        /// a weapon class with no fit yet answers false here, same as it does
        /// for GuardStops below.
        ///
        /// Tested against this tick's live blade pose only, not the sweep ring
        /// BeforeBlow also consults - unlike a one-shot swing hit, a guard
        /// re-evaluates every tick, so a single missed frame is not fatal.
        /// ponytail: simpler than sweeping; revisit if a fast pass is missed
        /// in a headset session.
        ///
        /// Always reports the CLOSEST attacking enemy found in reach, even
        /// when out of contact range, so VrMelee's "no guard" probe can say
        /// why - the return value is only whether that gap is within
        /// combat_block_slack of the combined radii.
        /// </summary>
        internal static bool TryWeaponBladeContact(Agent player, out float gap, out float limit,
                                                    out Agent contactAgent)
        {
            gap = float.MaxValue;
            limit = 0f;
            contactAgent = null;

            if (!Enabled || player == null)
                return false;

            // TEST 311: _live is indexed by HandRole. This read it by CONTROLLER
            // (Right = 1), which is the off hand's slot - a shield or nothing - so
            // the whole-blade test never ran and only BeforeBlow's at-impact test
            // was left, where only the handle is near the attacker's weapon.
            VrBlade.Segment mine = _live[(int)HandRole.Main];
            if (!mine.Valid)
                return false;

            Mission mission = Mission.Current;
            if (mission == null)
                return false;

            float reachSq = BlockContactReach * BlockContactReach;
            float bestGap = float.MaxValue;
            float bestLimit = 0f;
            Agent best = null;

            foreach (Agent candidate in mission.Agents)
            {
                // TEST 421: distance before the native IsEnemyOf call.
                if (candidate == null || candidate == player || !candidate.IsHuman
                    || !candidate.IsActive()
                    || (candidate.Position - player.Position).LengthSquared > reachSq
                    || !player.IsEnemyOf(candidate))
                    continue;

                Agent.ActionCodeType type;
                try { type = candidate.GetCurrentActionType(1); }
                catch (Exception) { continue; }

                if (type != Agent.ActionCodeType.ReadyMelee
                    && type != Agent.ActionCodeType.ReleaseMelee)
                    continue;

                EquipmentIndex slot = candidate.GetPrimaryWieldedItemIndex();
                if (slot == EquipmentIndex.None
                    || !VrBlade.TryGetEnemyBlade(candidate, slot, out VrBlade.Segment theirs))
                    continue;

                float d = SegmentDistance(mine.GripWorld, mine.TipWorld,
                                          theirs.GripWorld, theirs.TipWorld);

                if (d >= bestGap)
                    continue;

                bestGap = d;
                bestLimit = mine.Radius + theirs.Radius + BlockSlack;
                best = candidate;
            }

            if (best == null)
                return false;

            gap = bestGap;
            limit = bestLimit;
            contactAgent = best;

            if (bestGap > bestLimit)
                return false;

            // TEST 311: remembered, so a blow that lands before the engine's
            // guard took hold is still stopped by the touch (BeforeBlow).
            _contactIndex = best.Index;
            _contactAt = Stopwatch.GetTimestamp();
            _contactGap = bestGap;
            _contactLimit = bestLimit;
            return true;
        }

        // TEST 311: the last whole-blade contact - whose blade, when, how close.
        private static int _contactIndex = -1;
        private static long _contactAt;
        private static float _contactGap;
        private static float _contactLimit;

        /// <summary>How long a whole-blade contact keeps blocking that attacker's
        /// blow, in ms (TEST 311). The engine's guard is raised on the touch but can
        /// land a tick or two after the blow.</summary>
        private static readonly float BlockLatchMs = VrConfig.Float("combat_block_latch_ms", 150f);

        /// <summary>How far away an attacking enemy is even considered for the
        /// TEST 264 blade-contact guard test, in metres - a coarse prefilter
        /// before the real segment-distance test runs.</summary>
        private static readonly float BlockContactReach = VrConfig.Float("combat_block_reach_m", 3.0f);

        /// <summary>
        /// The victim's own weapon-guard action stops the player's physical hit
        /// (TEST 242; blade contact since TEST 265).
        ///
        /// A block in vanilla is a STATE: the defender raises the weapon, and
        /// the engine resolves the attack collision against it. TEST 242's
        /// reach zone tested only "is a Defend action active and is their
        /// weapon's REACH roughly toward the blade"; this is the same real
        /// contact test as TEST 264's own guard, against the SAME TEST-262-
        /// fitted line: the enemy's actual blade, not their weapon's length. A
        /// raised SHIELD is ShieldStops' job below and is skipped here - it
        /// used to be tested by this same reach zone too, which duplicated
        /// ShieldStops' own, more precise plate test with a cruder one.
        ///
        /// The action channel names the guard; the blade must reach the body's
        /// front for it to count - a guard does not stop an attack from
        /// behind. A weapon class with fewer than EnemyFitMinSamples real
        /// engine hits has no fitted blade yet, so this simply does not fire
        /// for it - see TryGetEnemyBlade.
        /// </summary>
        private static bool GuardStops(Agent victim, VrBlade.Segment blade,
                                       out string name, out float gap, out WeaponComponentData usage)
        {
            name = null;
            gap = float.MaxValue;
            usage = null;

            if (!EnemyGuard || victim == null || !victim.IsHuman || !blade.Valid)
                return false;

            try
            {
                for (int channel = 0; channel < 2; channel++)
                {
                    Agent.ActionCodeType type = victim.GetCurrentActionType(channel);
                    if (type < Agent.ActionCodeType.DefendAllBegin
                        || type >= Agent.ActionCodeType.DefendAllEnd)
                        continue;

                    // DefendShield sits inside the same DefendAllBegin..End
                    // range as every weapon parry (Agent.ActionCodeType, from
                    // the shipped DLL: DefendShield = 2, alongside DefendUp1h,
                    // DefendForward2h, DefendUpStaff...) - it is ShieldStops'
                    // job below, not this one's. Matching it here meant a
                    // shield-guarding victim's IDLE primary weapon got tested
                    // as if it were parrying: harmless for a short sword, but
                    // a long polearm's much bigger swept volume passed near
                    // that idle weapon often enough to force every hit's
                    // damage to 0 (MeleeHitCallback zeroes it whenever
                    // CollisionResult = Blocked and AttackBlockedWithShield is
                    // false - exactly what a false GuardStops match sets).
                    if (type == Agent.ActionCodeType.DefendShield)
                        continue;

                    EquipmentIndex slot = victim.GetPrimaryWieldedItemIndex();
                    WeaponComponentData candidate = slot != EquipmentIndex.None
                        ? Held(victim, slot) : null;

                    if (candidate == null || candidate.IsShield || !candidate.IsMeleeWeapon)
                        continue;

                    if (!VrBlade.TryGetEnemyBlade(victim, slot, out VrBlade.Segment theirs))
                        continue;

                    // A guard does not stop an attack from behind: the
                    // direction from the victim's chest to the PLAYER's blade
                    // must not run opposite their own facing.
                    Vec3 chest = Chest(victim);
                    Vec3 closest = ClosestOnSegment(chest, blade.GripWorld, blade.TipWorld);
                    Vec3 toBlade = closest - chest;

                    Vec3 facing = victim.LookDirection;
                    Vec3 flatBlade = new Vec3(toBlade.x, toBlade.y, 0f);
                    Vec3 flatFacing = new Vec3(facing.x, facing.y, 0f);

                    if (flatBlade.Length > 1e-4f && flatFacing.Length > 1e-4f
                        && Vec3.DotProduct(flatBlade, flatFacing) < -0.25f * flatBlade.Length)
                        continue;

                    float d = SegmentDistance(blade.GripWorld, blade.TipWorld,
                                              theirs.GripWorld, theirs.TipWorld);
                    float limit = blade.Radius + theirs.Radius + BlockSlack;

                    if (d > limit)
                        continue;

                    usage = candidate;
                    gap = d;
                    name = type.ToString() + (channel == 1 ? " (main)" : " (off)");
                    return true;
                }
            }
            catch (Exception)
            {
                // An unreadable guard is not a guard.
            }

            return false;
        }

        /// <summary>
        /// A raised shield stops a missile (TEST 243).
        ///
        /// Vanilla's rule is exactly this: arrows and bolts stop on a shield,
        /// and on a weapon only where a perk allows it. The engine decides a
        /// missile block from the defender's block STATE, and the player never
        /// plays one, so before this the VR shield was transparent to every
        /// arrow. The shield is a sphere read out of its plate; the missile's
        /// flight path is the line from where it was loosed to where it landed,
        /// and the plate must be on it, raised to chest level, on the side the
        /// shot came from.
        ///
        /// A miss while a raised shield existed is logged once, with the gap,
        /// so combat_block_arrow_slack is tuned from numbers like the melee one.
        /// </summary>
        private static bool MissileGuard(Mission mission, Agent attacker, Agent main, Agent victim,
                                         Blow blow, AttackCollisionData collisionData)
        {
            try
            {
                Vec3 impact = blow.GlobalPosition;
                Vec3 start = blow.WeaponRecord.StartingPosition;

                if ((start - impact).LengthSquared < 1e-4f)
                    start = blow.WeaponRecord.CurrentPosition;

                if ((start - impact).LengthSquared < 1e-4f)
                {
                    // No path on the record: step back along the flight from
                    // the impact, which is the line the missile actually flew.
                    Vec3 velocity = blow.WeaponRecord.Velocity;
                    if (velocity.LengthSquared < 1e-6f)
                        velocity = collisionData.MissileVelocity;
                    if (velocity.LengthSquared < 1e-6f)
                        velocity = blow.Direction;
                    if (velocity.LengthSquared < 1e-6f)
                        return false;

                    start = impact - velocity.NormalizedCopy() * 3f;
                }

                bool raised = false;
                float bestGap = float.MaxValue;
                float bestLimit = 0f;

                for (int role = 0; role < Roles; role++)
                {
                    VrBlade.Shield shield = _shield[role];
                    if (!shield.Valid || _shieldUsage[role] == null)
                        continue;

                    // Only a shield raised to chest level counts, in front of
                    // the torso and on the side the shot came from.
                    if (!ShieldUp(main, shield.CentreWorld, start))
                        continue;

                    raised = true;

                    float gap = PointSegment(shield.CentreWorld, start, impact);
                    float limit = shield.Radius + BlockArrowSlack;

                    if (gap < bestGap)
                    {
                        bestGap = gap;
                        bestLimit = limit;
                    }

                    if (gap <= limit)
                    {
                        Guard(mission, victim, attacker, blow, role, "shield",
                              "the arrow's flight path", gap, limit, 0f, -1f,
                              "TEST 243 arrow");

                        // TEST 305: the engine will still stick this missile -
                        // into the body bone it reached. MissileStickPatch moves
                        // it to where the flight crossed the plate.
                        _stick = new StickHandoff
                        {
                            Missile = blow.WeaponRecord.AffectorWeaponSlotOrMissileIndex,
                            Role = role,
                            Shift = ClosestOnSegment(shield.CentreWorld, start, impact) - impact,
                            At = Stopwatch.GetTimestamp(),
                        };
                        return true;
                    }
                }

                if (raised && BlockProbe)
                {
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 243 arrow pass: '{0}' landed {1} {2} - a raised shield was "
                        + "{3:F2} m from the flight path (limit {4:F2}); out of the way.",
                        attacker != null ? attacker.Name ?? "?" : "?",
                        blow.InflictedDamage, blow.DamageType, bestGap, bestLimit));
                }
            }
            catch (Exception)
            {
                // A path we cannot read is a missile we cannot block.
            }

            return false;
        }

        /// <summary>TEST 305: the missile MissileGuard just stopped, for
        /// MissileStickPatch - the same synchronous MissileHitCallback reaches
        /// both, so one record is enough.</summary>
        private struct StickHandoff
        {
            public int Missile;
            public int Role;
            public Vec3 Shift;
            public long At;
        }

        private static StickHandoff _stick;

        /// <summary>
        /// TEST 305: an arrow the VR shield stopped (MissileGuard) is about to be
        /// stuck into the player's body bone. Rebuild the engine's own world
        /// frame for it from that bone (the inverse of Mission.
        /// CalculateAttachedLocalFrame: the bone's unit-rotation world frame
        /// times the local frame), move it to where the flight crossed the plate,
        /// and express it in our shield copy's mesh frame - which is what the
        /// engine's attach list is local to - so the engine sticks it in the
        /// shield instead.
        /// </summary>
        internal static void RedirectStick(int missileIndex, ref MatrixFrame attachLocalFrame,
                                           Agent attachedAgent, ref bool attachedToShield, sbyte bone)
        {
            StickHandoff handoff = _stick;
            if (handoff.At == 0L || handoff.Missile != missileIndex
                || Stopwatch.GetTimestamp() - handoff.At > Seconds(0.5f))
                return;

            _stick.At = 0L;

            Agent main = Mission.Current?.MainAgent;
            if (main == null || attachedAgent != main)
                return;

            EquipmentIndex offSlot = main.GetOffhandWieldedItemIndex();
            if (offSlot == EquipmentIndex.None
                || !VrSkinnedWeapon.TryMeshWorld((HandRole)handoff.Role, out MatrixFrame meshWorld,
                                                 out EquipmentIndex shieldSlot)
                || shieldSlot != offSlot
                || !VrBlade.TryBoneWorld(main, bone, out MatrixFrame boneWorld))
                return;

            MatrixFrame stuck = boneWorld.GetUnitRotFrame(main.AgentScale).TransformToParent(attachLocalFrame);
            stuck.origin += handoff.Shift;

            attachLocalFrame = meshWorld.TransformToLocalNonOrthogonal(stuck);
            attachedToShield = true;

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 305 stick: missile {0} goes into your shield, not bone {1} - moved {2:F2} m "
                + "to where its flight crossed the plate.",
                missileIndex, bone, handoff.Shift.Length));
        }

        /// <summary>A block or a parry from the mod's own block test: a stronger
        /// pulse for a parry (the hand was moving against the blow).</summary>
        private static void Haptic(int role, bool parry)
        {
            Pulse(Controller(role),
                  parry ? Math.Min(1f, HapticAmplitude * 1.5f) : HapticAmplitude,
                  parry ? HapticMs * 1.5f : HapticMs);
        }

        /// <summary>TEST 261: the blade landed a hit. The pulse scales with the
        /// damage - combat_haptic_hit_min is a graze - so the hand says how the
        /// swing went, not just that it happened.</summary>
        private static void HitHaptic(int role, int damage)
        {
            if (HapticHit)
                Pulse(Controller(role), HapticAmplitude * HitScale(damage), HapticMs);
        }

        /// <summary>The hit pulse's amplitude fraction: combat_haptic_hit_min
        /// for nothing, full for 40 damage or more. Pure, so the test suite
        /// checks the mapping without a controller.</summary>
        private static float HitScale(int damage)
        {
            return Math.Max(HapticHitMin, Math.Min(1f, damage / 40f));
        }

        /// <summary>TEST 261: the enemy's guard or shield stopped the blade. A
        /// short, sharp pulse - a stopped swing, not a landed one.</summary>
        private static void ClashHaptic(int role)
        {
            if (HapticClash)
                Pulse(Controller(role), HapticClashAmp, HapticClashMs);
        }

        /// <summary>
        /// TEST 261: the ENGINE resolved one of your own blocks - the guard
        /// state VrMelee writes stopped an enemy blow, or a shield took an
        /// arrow. Called from PlayerBlockHapticPatch, which only fires for the
        /// player's own body. The hand that did the blocking gets the pulse:
        /// whichever one is holding a shield, otherwise the weapon hand.
        ///
        /// Every block is logged with what stopped it and the guard mode the
        /// engine had - that is the line that answers "is weapon blocking
        /// working" without guessing, since with motion melee on the mod's own
        /// block test never runs.
        ///
        /// Only while motion melee is on: with it off, blocking goes through
        /// the mod's own block test and Guard already pulsed.
        /// </summary>
        internal static void PlayerBlocked(bool shieldBlock, Agent attacker, Vec3 blowDirection)
        {
            if (!VrMelee.Enabled)
                return;

            int controller = (int)VrHandSlots.MainController;

            if (shieldBlock)
            {
                for (int role = 0; role < Roles; role++)
                {
                    if (_shield[role].Valid)
                    {
                        controller = Controller(role);
                        break;
                    }
                }
            }

            if (!_loggedBlockPulse)
            {
                _loggedBlockPulse = true;
                VrLog.Info("TEST 261: your own block now pulses the "
                           + (shieldBlock ? "shield" : "weapon") + " hand - the engine's "
                           + "OnAgentHitBlocked callback is wired to the haptic.");
            }

            try
            {
                Agent main = Mission.Current?.MainAgent;
                string mode = main != null ? main.CurrentGuardMode.ToString() : "?";

                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 261 block: '{0}' hit your {1} guard and was stopped - the engine's "
                    + "guard mode reads {2}, the blow came from ({3:F2}, {4:F2}, {5:F2}).",
                    attacker != null ? (attacker.Name ?? "?") : "?",
                    shieldBlock ? "shield" : "weapon", mode,
                    blowDirection.x, blowDirection.y, blowDirection.z));
            }
            catch (Exception)
            {
                // A log line is never worth a throw inside the block callback.
            }

            if (HapticBlock)
                Pulse(controller, HapticAmplitude, HapticMs);
        }

        private static int Controller(int role)
        {
            return (int)(role == 0 ? VrHandSlots.MainController : VrHandSlots.OffController);
        }

        /// <summary>
        /// One pulse on one controller, through the native side's real
        /// xrApplyHapticFeedback. A rate limit per hand keeps a cleave, a hit
        /// and a block on the same frame from stacking into one long buzz.
        /// A runtime that refuses it, or a session that is not ready, reports
        /// once rather than per hit.
        /// </summary>
        private static void Pulse(int controller, float amplitude, float ms)
        {
            if (!Haptics)
                return;

            try
            {
                long now = Stopwatch.GetTimestamp();
                int hand = controller == (int)VrHandSlots.OffController ? 1 : 0;

                if (now - _hapticAt[hand] < Seconds(0.05f))
                    return;

                _hapticAt[hand] = now;

                BvrStatus status = (BvrStatus)NativeMethods.bvr_trigger_haptic(
                    controller, Math.Min(1f, Math.Max(0f, amplitude)), ms / 1000f);

                if (status != BvrStatus.Ok && !_hapticsReported)
                {
                    _hapticsReported = true;
                    VrLog.Info("TEST 238: the block happened but no pulse was sent - the "
                               + "runtime or session refused it (status " + status + "). "
                               + "combat_haptic = 0 silences this.");
                }
                else if (status == BvrStatus.Ok && !_hapticsSent)
                {
                    _hapticsSent = true;
                    VrLog.Info("TEST 238c: first haptic pulse accepted by the runtime "
                               + "(amplitude " + HapticAmplitude.ToString(CultureInfo.InvariantCulture)
                               + ", " + HapticMs.ToString(CultureInfo.InvariantCulture) + " ms). "
                               + "If nothing can be felt, that is runtime or controller "
                               + "settings, not the call.");
                }
            }
            catch (Exception)
            {
                // A pulse is never worth a throw inside the damage path.
            }
        }

        /// <summary>Distance from a point to a segment, clamped at the ends.</summary>
        private static float PointSegment(Vec3 point, Vec3 a, Vec3 b)
        {
            Vec3 ab = b - a;
            float lengthSquared = ab.LengthSquared;
            if (lengthSquared < 1e-8f)
                return (point - a).Length;

            float t = Vec3.DotProduct(point - a, ab) / lengthSquared;
            t = Math.Max(0f, Math.Min(1f, t));

            return (point - (a + ab * t)).Length;
        }

        /// <summary>The shortest distance between two segments - the honest test
        /// for "did the two weapons touch". Textbook closest-points solve; the
        /// degenerate cases (a point for either segment) fall out of the guards.
        /// The out parameters say where on each segment the closest points sit
        /// (0 at the first endpoint, 1 at the second); the block probe prints
        /// the one on the player's blade so a hand block and a blade block are
        /// distinguishable in the log (TEST 239c).</summary>
        private static float SegmentDistance(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2)
        {
            return SegmentDistance(p1, q1, p2, q2, out float _, out float _);
        }

        internal static float SegmentDistance(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2,
                                              out float sAlong, out float tAlong)
        {
            Vec3 d1 = q1 - p1;
            Vec3 d2 = q2 - p2;
            Vec3 r = p1 - p2;

            float a = Vec3.DotProduct(d1, d1);
            float e = Vec3.DotProduct(d2, d2);

            float s;
            float t;

            if (a <= 1e-8f && e <= 1e-8f)
            {
                sAlong = 0f;
                tAlong = 0f;
                return r.Length;
            }

            if (a <= 1e-8f)
            {
                s = 0f;
                t = Clamp01(Vec3.DotProduct(d2, r) / e);
            }
            else
            {
                float c = Vec3.DotProduct(d1, r);

                if (e <= 1e-8f)
                {
                    t = 0f;
                    s = Clamp01(-c / a);
                }
                else
                {
                    float b = Vec3.DotProduct(d1, d2);
                    float denominator = a * e - b * b;

                    s = denominator > 1e-8f ? Clamp01((b * Vec3.DotProduct(d2, r) - c * e) / denominator) : 0f;
                    t = (b * s + Vec3.DotProduct(d2, r)) / e;

                    if (t < 0f)
                    {
                        t = 0f;
                        s = Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Clamp01((b - c) / a);
                    }
                }
            }

            sAlong = s;
            tAlong = t;
            return ((p1 + d1 * s) - (p2 + d2 * t)).Length;
        }

        /// <summary>Where a point sits along a blade: 0 at the grip, 1 at the
        /// tip, clamped. The block probe's "% along your blade".</summary>
        private static float SegmentPlace(VrBlade.Segment segment, Vec3 point)
        {
            Vec3 axis = segment.TipWorld - segment.GripWorld;
            float lengthSquared = axis.LengthSquared;
            if (lengthSquared < 1e-8f)
                return 0f;

            float t = Vec3.DotProduct(point - segment.GripWorld, axis) / lengthSquared;
            return t < 0f ? 0f : (t > 1f ? 1f : t);
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        /// <summary>Whether any sweep-ring pose is young enough to test.</summary>
        private static bool SweepHasAny(int role, long now)
        {
            for (int i = 0; i < SweepSlots; i++)
            {
                if (SweepFresh(role, i, now))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// A candidate blade direction (TEST 242): score it, and keep it when it
        /// explains the engine's along-blade distance better than what we have.
        /// Zero vectors are blows with no collision data (the RegisterBlow
        /// fallback) and are skipped.
        /// </summary>
        private static void TryCandidate(ref Vec3 best, ref float bestScore, ref string bestName,
                                         string name, Vec3 direction, Vec3 grip, Vec3 contact,
                                         float along)
        {
            float lengthSquared = direction.LengthSquared;
            if (lengthSquared < 1e-8f)
                return;

            Vec3 unit = direction * (1f / (float)Math.Sqrt(lengthSquared));
            float score = LineScore(grip, contact, unit, along);

            if (score < bestScore)
            {
                bestScore = score;
                best = unit;
                bestName = name;
            }
        }

        /// <summary>
        /// How badly a direction explains the engine's own collision. The grip
        /// (at the attach bone) should lie on the blade line - its perpendicular
        /// miss - and the contact should sit `along` metres from it. Both are in
        /// metres; the along error counts half because the grip miss is the more
        /// telling figure.
        /// </summary>
        private static float LineScore(Vec3 grip, Vec3 contact, Vec3 axis, float along)
        {
            Vec3 toContact = contact - grip;
            float projection = Vec3.DotProduct(toContact, axis);
            float perpendicular = (toContact - axis * projection).Length;

            return perpendicular + 0.5f * Math.Abs(projection - along);
        }

        /// <summary>The point on a segment nearest a point, for the guard test.</summary>
        private static Vec3 ClosestOnSegment(Vec3 point, Vec3 a, Vec3 b)
        {
            Vec3 ab = b - a;
            float lengthSquared = ab.LengthSquared;
            if (lengthSquared < 1e-8f)
                return a;

            float t = Clamp01(Vec3.DotProduct(point - a, ab) / lengthSquared);
            return a + ab * t;
        }

        /// <summary>One phrase for whichever test caught (or came closest to
        /// catching) the blow.</summary>
        private static string ContactText(string test, float distance, float limit)
        {
            return distance.ToString("0.00", CultureInfo.InvariantCulture) + " m from " + test
                   + " (limit " + limit.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        private static WeaponComponentData Held(Agent agent, EquipmentIndex slot)
        {
            MissionWeapon weapon = agent.Equipment[slot];
            return weapon.IsEmpty ? null : weapon.CurrentUsageItem;
        }

        private static long Seconds(float seconds)
        {
            return (long)(seconds * Stopwatch.Frequency);
        }

        // TEST 248's dupe window (NoteStruck/WasJustStruck/EngineBlowAlreadyStruck)
        // is gone (TEST 263): the hidden attack is ghosted outright now
        // (MeleeHitPatch), on every collision within its own window, not merely
        // deduplicated per victim after the fact.

        // --- the Phase 0 debug strike (TEST 236) ---------------------------

        /// <summary>
        /// The blow on the nearest enemy, through Agent.RegisterBlow. Kept
        /// exactly as TEST 236 proved it: a second way to land a hit that does
        /// not depend on the geometry, the swing windows or the raycasts, so a
        /// bad physical hit can always be compared against a known-good one.
        /// </summary>
        private static void StrikeNearest()
        {
            Mission mission = Mission.Current;
            Agent attacker = mission?.MainAgent;
            if (attacker == null || !attacker.IsActive() || !attacker.CombatActionsEnabled)
                return;

            if (!_loggedReady)
            {
                _loggedReady = true;
                VrLog.Info("TEST 236: the debug strike is armed on " + TestKey
                           + ". Press it in melee range of an enemy with a melee weapon in "
                           + "hand; the log will say what the engine did about it.");
            }

            MissionWeapon weapon = attacker.WieldedWeapon;
            WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;

            if (usage == null)
            {
                VrLog.Info("TEST 236 strike: the weapon hand is empty, so there is nothing to "
                           + "swing. Draw a melee weapon first.");
                return;
            }

            if (!usage.IsMeleeWeapon)
            {
                VrLog.Info("TEST 236 strike: the held item is not a melee weapon ("
                           + usage.WeaponClass + "). This path is melee only.");
                return;
            }

            Agent victim = NearestEnemy(mission, attacker);
            if (victim == null)
            {
                VrLog.Info("TEST 236 strike: no living enemy within "
                           + TestRange.ToString("0.0", CultureInfo.InvariantCulture)
                           + " m. combat_test_range raises that.");
                return;
            }

            int damage = TestDamage > 0f ? (int)TestDamage : usage.SwingDamage;
            if (damage < 1)
                damage = 1;

            // The chest, because a blow has to land somewhere and GetBoneTypeData
            // and the armor material both read this. A real strike takes the bone
            // from its own raycast; this uses the monster's own thorax bone.
            sbyte bone = victim.Monster != null && victim.Monster.ThoraxLookDirectionBoneIndex >= 0
                ? victim.Monster.ThoraxLookDirectionBoneIndex
                : (sbyte)0;

            sbyte itemBone = attacker.Monster != null ? attacker.Monster.MainHandItemBoneIndex : (sbyte)(-1);

            Vec3 hit = Chest(victim);
            Vec3 from = attacker.GetEyeGlobalPosition();
            Vec3 direction = (hit - from).NormalizedCopy();
            if (direction.LengthSquared < 1e-6f)
                direction = attacker.LookDirection;

            // BlowWeaponRecord.FillAsMeleeBlow is the engine's own filler: item
            // flags, weapon class, the slot the weapon came from, its weight and
            // whether the material is metal. It is what makes the hit sound, the
            // armor interaction and the kill feed name the right weapon.
            var blow = new Blow(attacker.Index);
            blow.WeaponRecord.FillAsMeleeBlow(weapon.Item, usage,
                                              (int)attacker.GetPrimaryWieldedItemIndex(),
                                              itemBone);
            blow.GlobalPosition = hit;
            blow.Direction = direction;
            blow.SwingDirection = direction;
            blow.StrikeType = StrikeType.Swing;
            blow.AttackType = AgentAttackType.Standard;
            blow.DamageType = usage.SwingDamageType;
            blow.BoneIndex = bone;
            blow.VictimBodyPart = BoneBodyPartType.Chest;
            blow.InflictedDamage = damage;
            blow.BaseMagnitude = damage;
            blow.MovementSpeedDamageModifier = 1f;
            blow.BlowFlag = BlowFlags.None;

            float before = victim.Health;
            float distance = (victim.Position - attacker.Position).Length;

            victim.RegisterBlow(blow, default(AttackCollisionData));

            float after = victim.Health;

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 236 strike: '{0}' [{1}] at {2:F2} m. '{3}' ({4}) asked the engine for "
                + "{5} {6} damage, {7} blow, bone {8} ({9} on the skeleton).",
                victim.Name ?? "?", victim.Index, distance,
                weapon.Item?.StringId ?? "?", usage.WeaponClass,
                damage, usage.SwingDamageType, StrikeType.Swing, bone, bone));

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 236 strike: health {0:F1} -> {1:F1} (the engine took {2:F1}), "
                + "InflictedDamage={3}, active={4}, state={5}.{6}",
                before, after, before - after,
                blow.InflictedDamage, victim.IsActive(), victim.State,
                after < 1f
                    ? " THE ENGINE'S Die() RAN - that is the whole phase: health, death and "
                      + "the reaction all came from the engine's damage path."
                    : " Health moved and the victim is still up; the engine's stagger and "
                      + "sound came with it. Kill it to prove Die as well."));
        }

        /// <summary>
        /// A point in the victim's chest, in world space. Agent.Position is the
        /// ground position, so the eye is the one height there is to work from;
        /// a third of a metre below it is the sternum on a human.
        /// </summary>
        internal static Vec3 Chest(Agent agent)
        {
            Vec3 eye = agent.GetEyeGlobalPosition();

            try
            {
                // A real body point when the skeleton is available. The thorax
                // bone is the one the monster definition itself names. It must
                // go through TryBoneWorld: the entitial frame alone is in the
                // entity's space, which is how the shield chest-band test
                // compared a world z against a local one and never passed.
                sbyte bone = agent.Monster != null ? agent.Monster.ThoraxLookDirectionBoneIndex : (sbyte)(-1);
                if (bone >= 0 && VrBlade.TryBoneWorld(agent, bone, out MatrixFrame frame))
                    return frame.origin;
            }
            catch (Exception)
            {
                // The framed fallback below is fine; a strike must not fail on a
                // skeleton read.
            }

            return new Vec3(eye.x, eye.y, eye.z - 0.33f, -1f);
        }

        /// <summary>The nearest living enemy of the player within TestRange, or
        /// null. Mounts and animals are skipped: a blown horse is a different
        /// blow and would not prove this one.</summary>
        private static Agent NearestEnemy(Mission mission, Agent attacker)
        {
            Agent best = null;
            float bestSquared = TestRange * TestRange;

            foreach (Agent candidate in mission.Agents)
            {
                if (candidate == null || candidate == attacker || !candidate.IsActive()
                    || candidate.IsMount || !candidate.IsHuman)
                    continue;

                if (!attacker.IsEnemyOf(candidate))
                    continue;

                float squared = (candidate.Position - attacker.Position).LengthSquared;
                if (squared >= bestSquared)
                    continue;

                bestSquared = squared;
                best = candidate;
            }

            return best;
        }
    }
}
