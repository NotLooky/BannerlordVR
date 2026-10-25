using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 271. Body holsters: reach to where a weapon lives and pull the
    /// trigger there to draw it, or to put it back if it is already out.
    ///
    ///     left shoulder, behind   either hand   bow
    ///     left hip                either hand   one-handed
    ///     right shoulder, behind  left hand     shield
    ///     right shoulder, behind  right hand    two-handed, polearm, thrown, crossbow, sling
    ///
    /// The engine does the swap (TryToWieldWeaponInSlot / TryToSheathWeaponInHand,
    /// Instant by default - the arm is ours, so the draw animation only delays
    /// it). Our weapon copy already follows slot changes (VrSkinnedWeapon).
    ///
    /// The trigger's vanilla Attack / Defend keys are off in a fight
    /// (trigger_attack = 0): swings and guards come from motion melee, arrows
    /// from VrBow. A crossbow, a throwing weapon or a sling has nothing else to
    /// fire it, so the trigger stays theirs outside the zones. A trigger claimed
    /// by a holster never reaches the game at all.
    ///
    /// The shoulders overlap VrBow's quiver on purpose. While a bow is out, the
    /// string hand's trigger behind the head is the arrow, and that hand ignores
    /// the shoulder zones; the bow hand puts the bow away.
    /// </summary>
    public static class VrHolster
    {
        public static bool Enabled { get; private set; } = VrConfig.Bool("holster", true);

        /// <summary>The VR panel's switch. Off also gives the trigger back.</summary>
        public static void SetEnabled(bool on)
        {
            Enabled = on;
            Clear();
        }

        private static readonly bool TriggerAttack = VrConfig.Bool("trigger_attack", false);

        /// <summary>TEST 370: a press only draws; the grip's release puts it back
        /// (VrGrab).</summary>
        private static readonly bool HoldConfig = VrConfig.Bool("grip_hold", true);
        private static bool HoldOn => VrGrip.Toggle || HoldConfig;

        /// <summary>0 = WithAnimation, if Instant misbehaves on the player.</summary>
        private static readonly Agent.WeaponWieldActionType WieldType =
            VrConfig.Bool("holster_instant", true)
                ? Agent.WeaponWieldActionType.Instant
                : Agent.WeaponWieldActionType.WithAnimation;

        private static readonly bool Probe = VrConfig.Bool("holster_probe", true);

        /// <summary>
        /// TEST 271b: the back is VrBow's quiver test - the hand behind the eyes by
        /// holster_back_behind_m, no lower than holster_back_low_m below them - and
        /// the side of the head picks the shoulder. 271's spheres 0.18 m out to
        /// each side missed: over-the-shoulder reaches landed near the middle of
        /// the back of the head (x -0.13..0.00, 0.12-0.22 m behind the eyes).
        /// Head-local (right, forward, up) metres.
        /// </summary>
        /// 271c: 0.05 behind let a hand merely beside the head count (the eyes sit
        /// ~0.1 m in front of the ears); the real reaches were 0.12-0.22 behind.
        /// holster_back_side_m keeps a hand held out to the side out of it.
        /// TEST 318 (0.09, was 0.15): two over-the-shoulder reaches at 0.12 behind
        /// missed the line; the user asked for the zone 6 cm further forward.
        /// TEST 388: ONE back zone for the holsters, the quiver and the bolts, in
        /// the drawn body's frame from the ears (VrBow.BodyLocal): behind the ear
        /// line by back_zone_behind_m, no lower than back_zone_low_m below the head
        /// bone, within back_zone_side_m of the middle, either shoulder.
        /// holster_back_*, bow_quiver_* and xbow_bolt_* are no longer read.
        /// TEST 411: the user's rule is now "on ear to behind ear" (2026-10-06, PSVR2:
        /// on-the-ear reaches logged 0-7 cm ahead of the line and missed), so a
        /// negative value reaches ahead of the ears - at most 0.15 m, and only at
        /// ear height (EarBandM): a hand at the side of the shoulder stays out.
        private static readonly float BackBehindM = Math.Max(-0.15f, VrConfig.Float("back_zone_behind_m", 0f));
        private static readonly float BackLowM = Math.Max(0f, VrConfig.Float("back_zone_low_m", 0.20f));
        private static readonly float BackSideM = Math.Max(0f, VrConfig.Float("back_zone_side_m", 0.30f));

        /// <summary>The hip sphere, only without VrHolsterView. TEST 388: from the
        /// ears now (was the eyes: 0.10 m forward, 0.14 m up of them).</summary>
        private static readonly Vec3 HipCentre =
            VrBow.ParseZone(VrConfig.String("holster_zone_hip", "-0.20,0.15,-0.56")) ?? new Vec3(-0.20f, 0.15f, -0.56f);

        private static readonly float HipRadius = VrConfig.Float("holster_zone_radius", 0.20f);

        private enum Zone { None, LeftShoulder, RightShoulder, LeftHip }

        // The VrBow trigger hysteresis.
        private const float TriggerDown = 0.7f;
        private const float TriggerUp = 0.4f;

        // Per controller, indexed by VrHand.
        private static readonly bool[] _down = new bool[2];
        private static readonly bool[] _inZone = new bool[2];
        private static readonly bool[] _latched = new bool[2];

        /// <summary>The last slot drawn per zone and hand, so two items in one
        /// zone take turns.</summary>
        private static readonly EquipmentIndex[] _last = NewLast();

        private static readonly List<EquipmentIndex> _items = new List<EquipmentIndex>();

        /// <summary>TEST 333: every agent the player takes starts with empty hands
        /// - weapons and shield in their holsters, drawn from there.</summary>
        private static readonly bool SpawnBare = VrConfig.Bool("spawn_bare_hands", true);
        private static Agent _bared;

        private static bool _broken;
        private static bool _loggedOn;
        private static long _logAt;

        /// <summary>One frame, from AfterMissionTick after VrBow.</summary>
        public static void Tick()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                Agent agent = Mission.Current?.MainAgent;
                if (!Ready(agent) || !VrCameraDriver.HasPose
                    || NativeMethods.bvr_get_input_state(out BvrInputState input, battleGrip: true) != (int)BvrStatus.Ok)
                {
                    Clear();
                    return;
                }

                if (!_loggedOn)
                {
                    _loggedOn = true;
                    VrLog.Info("TEST 271: holsters are on - trigger over the left shoulder = bow, left hip = "
                               + "one-handed, right shoulder = shield (left hand) or two-handed / polearm / "
                               + "thrown / crossbow (right hand). trigger_attack = " + (TriggerAttack ? 1 : 0)
                               + ". TEST 318: zones are measured from the calibrated hand ('"
                               + VrHandCalibration.ControllerName + "' controller grip); TEST 388: "
                               + BackZoneText + ".");
                }

                if (SpawnBare && !ReferenceEquals(_bared, agent))
                {
                    _bared = agent;
                    Bare(agent);
                }

                Hand(agent, VrHand.Left, input.Left);
                Hand(agent, VrHand.Right, input.Right);
            }
            catch (Exception ex)
            {
                _broken = true;
                Clear();
                VrLog.Error("TEST 271: holsters failed; they are off for this session and the trigger "
                            + "goes back to the game.", ex);
            }
        }

        /// <summary>Whether the game may see this controller's trigger as its
        /// Attack / Defend key. Asked from VrGamepad once per frame.</summary>
        internal static bool TriggerKeyAllowed(bool left)
        {
            // TEST 571: the order menu's move-to-flag is confirmed by the right
            // trigger (OrderTroopPlacer reads LeftMouseButton or ControllerRTrigger),
            // and with a melee weapon out the rules below kept it from the game.
            // Pre-battle the right grip clicks; the trigger only aims the laser.
            if (VrMissionPhase.OrderMenuOpen && !VrMissionPhase.Deploying)
                return true;

            if (VrGrip.ConsumesPress(left))
                return false;
            int i = left ? (int)VrHand.Left : (int)VrHand.Right;
            if (Enabled && !_broken && (_inZone[i] || _latched[i]))
                return false;

            // TEST 332: a press that picked something up is not an attack.
            if (VrGrab.Latched(left))
                return false;

            // TEST 473: nor a hand on the tiller.
            if (VrHelm.Holds(left))
                return false;

            // TEST 512: nor a hand on a siege machine.
            if (VrSiege.Holds(left))
                return false;

            // TEST 370: a grip that holds is a hand, not a game key - a held
            // javelin or crossbow can no longer start the vanilla throw or shot.
            // A sling has no manual mode and keeps the trigger.
            if (VrGrab.Holding(left ? VrHand.Left : VrHand.Right) && !SlingInHand())
                return false;

            // TEST 373: every grip is a hand while the crossbow is out - the
            // stock, the fore-stock, a bolt, the string.
            if (VrCrossbow.Active)
                return false;

            // Holsters off is the old trigger: trigger_attack only means something
            // while the trigger has another job.
            if (TriggerAttack || !Enabled || _broken)
                return true;

            try
            {
                Agent agent = Mission.Current?.MainAgent;
                if (!Ready(agent))
                    return true;

                MissionWeapon held = agent.WieldedWeapon;
                return !held.IsEmpty && held.CurrentUsageItem != null && held.CurrentUsageItem.IsRangedWeapon;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool SlingInHand()
        {
            try
            {
                WeaponComponentData usage = Mission.Current?.MainAgent?.WieldedWeapon.CurrentUsageItem;
                return usage != null && usage.WeaponClass == WeaponClass.Sling;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Whether this controller was in a holster zone that holds
        /// something for it, as of the last tick. VrGrab defers to it.</summary>
        internal static bool InZone(VrHand hand) => Enabled && !_broken && _inZone[(int)hand];

        private static void Hand(Agent agent, VrHand hand, BvrHandState state)
        {
            int i = (int)hand;
            if (state.IsActive == 0)
            {
                _down[i] = _inZone[i] = _latched[i] = false;
                return;
            }

            bool pressed = state.Trigger >= (_down[i] ? TriggerUp : TriggerDown);
            bool edge = pressed && !_down[i];
            _down[i] = pressed;
            if (!pressed)
                _latched[i] = false;

            Vec3 world = VrWeaponHands.HandPoint(hand, state);
            Vec3 local = VrBow.BodyLocal(world);
            Zone zone = ZoneAt(local);
            if (zone != Zone.LeftHip && (VrBow.StringController == hand || VrCrossbow.LoadController == hand))
                zone = Zone.None;

            // TEST 304: a hand holding a two-handed weapon's shaft is not
            // reaching for a holster - its trigger is the grip, and while two
            // hands hold it the main trigger slides that hand (TEST 304g).
            if (VrTwoHand.Gripping != null)
                zone = Zone.None;

            // TEST 329: a weapon is taken where it is SEEN hanging, and put back
            // where it hangs (VrHolsterView's copies, both hips and the back);
            // the head-relative hip sphere is only the fallback without them.
            // TEST 373: the ammo hand behind the head is reaching for an arrow or
            // a bolt, not for the javelins hanging on the back.
            bool ammoReach = (VrBow.StringController == hand || VrCrossbow.LoadController == hand)
                             && BackZone(local);

            EquipmentIndex near = EquipmentIndex.None;
            if (VrTwoHand.Gripping == null && !ammoReach
                && VrHolsterView.Near(world, s => Allowed(agent, s, hand), out near))
            {
                zone = Zone.LeftHip;
                _items.Clear();
                _items.Add(near);
            }
            else
            {
                if (zone == Zone.LeftHip && VrHolsterView.Enabled)
                    zone = Zone.None;

                Items(agent, zone, hand);

                // 271b: the side only matters when this hand has something on both
                // shoulders; otherwise anywhere behind the head is the back.
                if (_items.Count == 0 && (zone == Zone.LeftShoulder || zone == Zone.RightShoulder))
                {
                    zone = zone == Zone.LeftShoulder ? Zone.RightShoulder : Zone.LeftShoulder;
                    Items(agent, zone, hand);
                }
            }

            _inZone[i] = _items.Count > 0;

            if (!edge)
                return;

            // TEST 332b: a press the pickup already used this frame (VrGrab ticks
            // first). Crouched by a hip zone, the same press picked the sword up
            // and sheathed it 2 ms later.
            if (VrGrab.Latched(hand == VrHand.Left))
                return;

            if (!_inZone[i])
            {
                if (Probe && VrBow.StringController != hand && VrCrossbow.LoadController != hand
                    && Stopwatch.GetTimestamp() >= _logAt)
                {
                    _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    VrLog.Info("TEST 271 no holster: " + hand + " trigger at (" + F(local.x) + ", " + F(local.y)
                               + ", " + F(local.z) + ") body-local from the ears (right, fwd, up), zone " + zone
                               + ", hip d=" + F((local - HipCentre).Length) + "; carrying " + Carried(agent) + ".");
                }
                return;
            }

            _latched[i] = true;
            Toggle(agent, zone, hand, (near != EquipmentIndex.None ? "its holster" : zone.ToString())
                                      + " at (" + F(local.x) + ", " + F(local.y) + ", " + F(local.z) + ")");
            NativeMethods.bvr_trigger_haptic((int)hand, 0.6f, 0.05f);
        }

        /// <summary>TEST 333: put away whatever the agent spawned holding, the
        /// first frame VR controls it.</summary>
        private static void Bare(Agent agent)
        {
            string main = agent.WieldedWeapon.Item?.StringId;
            string off = agent.WieldedOffhandWeapon.Item?.StringId;
            if (main == null && off == null)
                return;

            if (off != null)
                agent.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.Instant);
            if (main != null)
                agent.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.Instant);

            VrLog.Info("TEST 333: spawned holding '" + (main ?? "-") + "' / '" + (off ?? "-") + "'; both put "
                       + "away, so the hands start empty.");
        }

        /// <summary>Sheathe the zone's item if one is out, else draw the next.</summary>
        private static void Toggle(Agent agent, Zone zone, VrHand hand, string where)
        {
            EquipmentIndex main = agent.GetPrimaryWieldedItemIndex();
            EquipmentIndex off = agent.GetOffhandWieldedItemIndex();

            foreach (EquipmentIndex slot in _items)
            {
                if (slot != main && slot != off)
                    continue;

                // TEST 370: out already, it goes back when the grip lets go of
                // it here - a press never puts anything away.
                if (HoldOn)
                    return;

                Agent.HandIndex which = slot == main ? Agent.HandIndex.MainHand : Agent.HandIndex.OffHand;
                VrLog.Info("TEST 271 holster: " + hand + " in " + where + " -> sheathe " + which
                           + " '" + agent.Equipment[slot].Item?.StringId + "'.");
                agent.TryToSheathWeaponInHand(which, WieldType);
                return;
            }

            int k = (int)zone * 2 + (int)hand;
            EquipmentIndex next = _items[0];
            foreach (EquipmentIndex slot in _items)
            {
                if (slot > _last[k])
                {
                    next = slot;
                    break;
                }
            }

            _last[k] = next;

            // TEST 272: the draw is instant; VrSkinnedWeapon freezes the grip once
            // the animation settles. The timestamp only labels that log line.
            VrLog.Info("TEST 271 holster: " + hand + " in " + where + " -> draw slot " + next
                       + " '" + agent.Equipment[next].Item?.StringId + "'.");
            VrSkinnedWeapon.HolsterDrawAt = Stopwatch.GetTimestamp();

            // TEST 329: out of the holster, not out of nothing - the weapon glides
            // from where it hangs into the hand that will hold it.
            if (VrHolsterView.TryVisual(next, out MatrixFrame from))
                VrSkinnedWeapon.Glide(VrGrab.EntityRole(agent.Equipment[next].Item), from);

            agent.TryToWieldWeaponInSlot(next, WieldType, false);
        }

        /// <summary>Whether this hand may take this slot's item from, or put it
        /// back in, its holster (Rule's hand restriction).</summary>
        private static bool Allowed(Agent agent, EquipmentIndex slot, VrHand hand)
        {
            ItemObject item = agent.Equipment[slot].Item;
            return item != null && Rule(item.ItemType, out VrHand? only) != Zone.None && (only == null || only == hand);
        }

        /// <summary>The weapon slots this zone holds for this hand, into _items.</summary>
        private static void Items(Agent agent, Zone zone, VrHand hand)
        {
            _items.Clear();
            if (zone == Zone.None)
                return;

            for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
            {
                ItemObject item = agent.Equipment[slot].Item;
                if (item != null && Rule(item.ItemType, out VrHand? only) == zone && (only == null || only == hand))
                    _items.Add(slot);
            }
        }

        /// <summary>Where an item type lives, and the one hand that may take it
        /// (null = either).</summary>
        private static Zone Rule(ItemObject.ItemTypeEnum type, out VrHand? only)
        {
            only = null;
            switch (type)
            {
                case ItemObject.ItemTypeEnum.Bow:
                    return Zone.LeftShoulder;
                case ItemObject.ItemTypeEnum.OneHandedWeapon:
                    return Zone.LeftHip;
                case ItemObject.ItemTypeEnum.Shield:
                    only = VrHand.Left;
                    return Zone.RightShoulder;
                case ItemObject.ItemTypeEnum.Crossbow:
                    // TEST 373c: the left hand is the crossbow's holder; the right
                    // comes to the stock once it is out.
                    only = VrHandSlots.OffController;
                    return Zone.RightShoulder;
                case ItemObject.ItemTypeEnum.TwoHandedWeapon:
                case ItemObject.ItemTypeEnum.Polearm:
                case ItemObject.ItemTypeEnum.Thrown:
                case ItemObject.ItemTypeEnum.Sling:
                    only = VrHand.Right;
                    return Zone.RightShoulder;
                default:
                    return Zone.None;
            }
        }

        /// <summary>TEST 388: behind the ears, over either shoulder, in body-local
        /// metres (VrBow.BodyLocal). The holsters, the quiver and the bolts all
        /// ask this one rule.</summary>
        internal static bool BackZone(Vec3 local)
        {
            float behind = local.z >= -EarBandM ? BackBehindM : Math.Max(0f, BackBehindM);
            return local.y <= -behind && local.z >= -BackLowM && Math.Abs(local.x) <= BackSideM;
        }

        /// <summary>TEST 411: how far below the head bone a hand still counts as at
        /// the ear (the PSVR2 on-the-ear reaches sat -0.02 to +0.05).</summary>
        private const float EarBandM = 0.10f;

        internal static string BackZoneText =>
            "the back zone is behind the ears by " + F(BackBehindM) + " m, down to " + F(BackLowM)
            + " m below them, within " + F(BackSideM) + " m of the middle";

        /// <summary>Behind the ears is a shoulder, by side; else the hip sphere.</summary>
        private static Zone ZoneAt(Vec3 local)
        {
            if (BackZone(local))
                return local.x < 0f ? Zone.LeftShoulder : Zone.RightShoulder;

            return (local - HipCentre).Length <= HipRadius ? Zone.LeftHip : Zone.None;
        }

        /// <summary>The item types in the weapon slots, for the miss line.</summary>
        private static string Carried(Agent agent)
        {
            var text = new System.Text.StringBuilder();
            for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
            {
                ItemObject item = agent.Equipment[slot].Item;
                if (item != null)
                    text.Append(text.Length > 0 ? ", " : "").Append(item.ItemType);
            }
            return text.Length > 0 ? text.ToString() : "nothing";
        }

        /// <summary>The VrBow.Owns conditions without the bow.</summary>
        internal static bool Ready(Agent agent)
        {
            if (agent == null || !agent.IsActive() || !agent.CombatActionsEnabled)
                return false;

            // TEST 341: at a helm, oar or siege weapon the trigger is the game's
            // Attack again - the key a ballista fires on.
            if (agent.IsUsingGameObject)
                return false;

            if (!VrSystem.IsActive || !VrMissionPhase.VrDrivesLook || VrMissionPhase.UiMenuOpen || VrMenu.Open)
                return false;

            Mission mission = Mission.Current;
            return mission != null && mission.Mode != MissionMode.Conversation && mission.Mode != MissionMode.Barter;
        }

        private static void Clear()
        {
            for (int i = 0; i < 2; i++)
                _down[i] = _inZone[i] = _latched[i] = false;
        }

        private static EquipmentIndex[] NewLast()
        {
            EquipmentIndex[] last = new EquipmentIndex[8];
            for (int i = 0; i < last.Length; i++)
                last[i] = EquipmentIndex.None;
            return last;
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
