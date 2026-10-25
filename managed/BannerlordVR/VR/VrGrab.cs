using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BannerlordVR.Interop;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Taking things with the hand (B&amp;S plan, Phases 3-4).
    ///
    /// TEST 332: the grip, pressed with the hand within grab_reach_m of a weapon
    /// lying in the world, picks it up. Vanilla's own Agent.OnItemPickup does
    /// the rest - it drops whatever the chosen slot held, equips the ground
    /// entity itself as the slot entity and wields it - and VrSkinnedWeapon
    /// glides the weapon from where it lay into the fist (TEST 331b).
    ///
    /// Which slot: the one the grabbing hand already holds, so that weapon drops
    /// (the user's "weapon drops when I take another"); with the hand empty, a
    /// free slot; with all four full, the slot this hand held last. Ammo tops up
    /// the vanilla way.
    ///
    /// Which hand: the one Bannerlord holds the item in. The off controller
    /// takes shields (and bows, which are carried on the off wrist); the main
    /// controller takes everything else. The wrong hand gets a short buzz.
    ///
    /// Ticks BEFORE VrWeaponHands, so the same frame's Drive copies the new slot
    /// entity and takes the engine's meshes off before it is ever drawn in the
    /// animation's hand.
    ///
    /// TEST 370 (grip_hold = 1): the grip HOLDS. A hand holds whatever it
    /// carries - drawn from a holster, picked up, caught, or wielded by a key -
    /// and the grip's release lets it go: back into its holster when let go at
    /// it, otherwise thrown or tossed (VrThrow), and on the engine side
    /// sheathed at once. No hold state of its own: what the hand carries IS the
    /// hold, so nothing can disagree with the engine. A release the hand did
    /// not make is never one - paused, a menu, a conversation, or the
    /// controller untracked: the grip is followed silently and the weapon stays.
    /// The bow's string hand and the crossbow (VrCrossbow, which holds with both
    /// hands) are their own.
    /// </summary>
    internal static class VrGrab
    {
        private static readonly bool HoldConfig = VrConfig.Bool("grip_hold", true);
        private static bool HoldOn => VrGrip.Toggle || HoldConfig;
        private static readonly bool PickupOn = VrConfig.Bool("grab_pickup", true);
        private static readonly float ReachM = VrConfig.Float("grab_reach_m", 0.35f);
        private static readonly bool Probe = VrConfig.Bool("grab_probe", true);

        private const float Down = 0.7f;
        private const float Up = 0.4f;
        private const int Max = 128;

        // The engine's own retire flags on a picked-up ground item (private).
        internal static readonly FieldInfo ReadyToDelete = AccessTools.Field(typeof(SpawnedItemEntity), "_readyToBeDeleted");
        internal static readonly PropertyInfo PhysicsStopped = AccessTools.Property(typeof(SpawnedItemEntity), "PhysicsStopped");

        private static readonly bool[] _down = new bool[2];
        private static readonly bool[] _latched = new bool[2];
        private static readonly EquipmentIndex[] _lastHeld = { EquipmentIndex.None, EquipmentIndex.None };
        private static readonly WeakGameEntity[] _hits = new WeakGameEntity[Max];
        private static readonly UIntPtr[] _ids = new UIntPtr[Max];
        private static Mission _mission;
        private static long _logAt;

        /// <summary>TEST 370c: the two-hander the main hand let go of while the
        /// off hand still held its shaft - kept until both have let go.</summary>
        private static EquipmentIndex _mainLetGo = EquipmentIndex.None;
        private static bool _broken;
        private static bool _loggedOn;

        /// <summary>Whether this controller's grip belongs to a grab this press,
        /// so the game must not also see it (VrHolster.TriggerKeyAllowed).</summary>
        internal static bool Latched(bool left) => _latched[left ? 0 : 1];

        internal static void Tick()
        {
            if ((!PickupOn && !HoldOn) || _broken)
                return;

            try
            {
                if (NativeMethods.bvr_get_input_state(out BvrInputState input, battleGrip: true) != (int)BvrStatus.Ok)
                {
                    Clear();
                    return;
                }

                // TEST 370: never a release the hand did not make - the grip is
                // followed, so letting go during a pause or a menu changes nothing.
                Agent agent = Mission.Current?.MainAgent;
                if (!VrHolster.Ready(agent) || VrWeaponHands.Paused())
                {
                    Follow(VrHand.Left, input.Left);
                    Follow(VrHand.Right, input.Right);
                    return;
                }

                if (!ReferenceEquals(_mission, Mission.Current))
                {
                    _mission = Mission.Current;
                    _lastHeld[0] = _lastHeld[1] = EquipmentIndex.None;
                }

                if (!_loggedOn)
                {
                    _loggedOn = true;
                    VrLog.Info("TEST 332: grip within " + F(ReachM) + " m of a weapon on the ground picks it up; "
                               + "the hand's own weapon drops, else a free slot, else the slot this hand held last. "
                               + "TEST 370: grip_hold = " + (HoldOn ? 1 : 0) + (HoldOn
                                   ? " - the grip holds; let go at a holster puts it back, elsewhere it is thrown or tossed."
                                   : " - the grip toggles (TEST 271)."));
                }

                Remember(agent);
                VrThrow.Feed(agent, VrHand.Left, input.Left);
                VrThrow.Feed(agent, VrHand.Right, input.Right);
                Hand(agent, VrHand.Left, input.Left);
                Hand(agent, VrHand.Right, input.Right);
                BothLetGo(agent, VrHandSlots.MainController == VrHand.Left ? input.Left : input.Right);
            }
            catch (Exception ex)
            {
                _broken = true;
                Clear();
                VrLog.Error("TEST 332/370: picking up and holding failed; they are off for this session.", ex);
            }
        }

        /// <summary>TEST 370c: the two-hander the main hand let go of is dropped
        /// once the off hand lets go of its shaft too.</summary>
        private static void BothLetGo(Agent agent, BvrHandState mainState)
        {
            if (_mainLetGo == EquipmentIndex.None)
                return;

            // Changed, or the main grip is back on it (even one seen untracked).
            if (agent.GetPrimaryWieldedItemIndex() != _mainLetGo || _down[(int)VrHandSlots.MainController])
            {
                _mainLetGo = EquipmentIndex.None;
                return;
            }

            if (VrTwoHand.Gripping != null)
                return;

            EquipmentIndex slot = _mainLetGo;
            _mainLetGo = EquipmentIndex.None;
            Let(agent, VrHandSlots.MainController, VrWeaponHands.HandPoint(VrHandSlots.MainController, mainState),
                slot, null);
        }

        /// <summary>TEST 370: the grip's state, with nothing done about it.</summary>
        private static void Follow(VrHand hand, BvrHandState state)
        {
            int i = (int)hand;
            _down[i] = state.Trigger >= (_down[i] ? Up : Down);
            if (!_down[i])
                _latched[i] = false;
        }

        /// <summary>TEST 370: what this controller holds - the slot its wrist
        /// carries (a bow rides the off wrist), or None.</summary>
        internal static EquipmentIndex Carried(Agent agent, VrHand hand)
        {
            EquipmentIndex main = agent.GetPrimaryWieldedItemIndex();
            EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
            WeaponComponentData usage = main == EquipmentIndex.None ? null : agent.Equipment[main].CurrentUsageItem;
            bool mainOnOffWrist = VrHandSlots.RidesOffWrist(usage);

            if (hand == VrHandSlots.MainController)
                return mainOnOffWrist ? EquipmentIndex.None : main;

            return off != EquipmentIndex.None ? off : mainOnOffWrist ? main : EquipmentIndex.None;
        }

        /// <summary>TEST 370: whether this controller holds something under
        /// grip_hold - its grip is then never a game key (VrHolster) and the panel
        /// chord does not count (VrMenu).</summary>
        internal static bool Holding(VrHand hand)
        {
            // TEST 512d: a hand on a siege machine, the ladder, the tiller or the
            // rope holds too - both grips there opened the VR panel.
            bool left = hand == VrHand.Left;
            if (VrSiege.Holds(left) || VrLadder.Holds(hand) || VrHelm.Holds(left) || VrRein.HeldBy == hand)
                return true;

            if (!HoldOn || _broken)
                return false;

            try
            {
                Agent agent = Mission.Current?.MainAgent;
                if (!VrHolster.Ready(agent))
                    return false;

                if (VrCrossbow.Owns(agent))
                    return VrCrossbow.Holds(hand);

                return Carried(agent, hand) != EquipmentIndex.None;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// TEST 370: the hand let go of <paramref name="slot"/> at
        /// <paramref name="at"/>. At its own holster it goes straight in (the
        /// holster view glides it home); anywhere else VrThrow throws it or tosses
        /// a copy. Either way the engine sheathes it now. <paramref name="seen"/>
        /// is where it is shown, when that is not VrSkinnedWeapon's frame.
        /// </summary>
        internal static void Let(Agent agent, VrHand hand, Vec3 at, EquipmentIndex slot, MatrixFrame? seen)
        {
            HandRole role = slot == agent.GetPrimaryWieldedItemIndex() ? HandRole.Main : HandRole.Off;
            string id = agent.Equipment[slot].Item?.StringId;

            bool home = VrHolsterView.Near(at, s => s == slot, out _);
            bool threw = !home && VrThrow.Release(agent, hand, slot, role, seen);

            // TEST 474: a rock or pot let go without a throw falls where it is -
            // and can be taken off the ground again (Usable).
            if (!threw && slot == EquipmentIndex.ExtraWeaponSlot && Heavy(agent.Equipment[slot]))
            {
                agent.DropItem(slot);
                NativeMethods.bvr_trigger_haptic((int)hand, 0.25f, 0.03f);
                VrLog.Info("TEST 474 drop: " + hand + " let go of '" + id + "' without a throw - dropped.");
                return;
            }

            agent.TryToSheathWeaponInHand(role == HandRole.Main ? Agent.HandIndex.MainHand : Agent.HandIndex.OffHand,
                                          Agent.WeaponWieldActionType.Instant);
            NativeMethods.bvr_trigger_haptic((int)hand, 0.25f, 0.03f);

            if (Probe)
                VrLog.Info("TEST 370 release: " + hand + " let go of '" + id + "' (slot " + slot + ") "
                           + (home ? "at its holster - straight in." : "away from its holster."));
        }

        private static void Remember(Agent agent)
        {
            EquipmentIndex main = agent.GetPrimaryWieldedItemIndex();
            EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
            if (main != EquipmentIndex.None)
                _lastHeld[(int)HandRole.Main] = main;
            if (off != EquipmentIndex.None)
                _lastHeld[(int)HandRole.Off] = off;
        }

        private static void Hand(Agent agent, VrHand hand, BvrHandState state)
        {
            int i = (int)hand;

            // TEST 370: untracked, the hand has no place to act at - and a
            // release seen now would throw with a velocity it does not have.
            if (state.IsActive == 0)
            {
                Follow(hand, state);
                return;
            }

            bool pressed = state.Trigger >= (_down[i] ? Up : Down);
            bool edge = pressed && !_down[i];
            bool released = !pressed && _down[i];
            _down[i] = pressed;
            if (!pressed)
                _latched[i] = false;

            // TEST 475: the off hand under the rock lets go of nothing but it.
            if (released && VrWeight.SupportHand == hand)
            {
                VrWeight.SupportHand = null;
                return;
            }

            // TEST 511: a hand on the ladder lets go of nothing but it.
            if (released && VrLadder.Holds(hand))
            {
                VrLadder.Let(hand);
                return;
            }

            if (released && HoldOn && VrBow.StringController != hand && !VrCrossbow.Owns(agent))
            {
                EquipmentIndex held = Carried(agent, hand);

                // TEST 370c: a two-hander is let go only when BOTH hands are off
                // it - the main grip let go to re-take or slide the grip (304g)
                // while the off hand holds the shaft keeps it.
                if (held != EquipmentIndex.None && hand == VrHandSlots.MainController
                    && VrTwoHand.Gripping == VrHandSlots.OffController && held == agent.GetPrimaryWieldedItemIndex())
                {
                    _mainLetGo = held;
                    if (Probe)
                        VrLog.Info("TEST 370c: the main hand let go of '" + agent.Equipment[held].Item?.StringId
                                   + "' but the off hand holds its shaft - kept until both let go.");
                }
                else if (held != EquipmentIndex.None)
                {
                    Let(agent, hand, VrWeaponHands.HandPoint(hand, state), held, null);
                }

                return;
            }

            if (!edge)
                return;

            // TEST 370c: the main hand coming back to the two-hander it let go of
            // (its slide, if the off hand holds) - never a pickup or a holster.
            if (hand == VrHandSlots.MainController && _mainLetGo != EquipmentIndex.None)
            {
                _mainLetGo = EquipmentIndex.None;
                _latched[i] = true;
                return;
            }

            // Every other owner of this press goes first: a holster in reach (its
            // zone is last tick's), the two-hand grip, the bow's string, the
            // crossbow's loading hand.
            if (VrHolster.InZone(hand) || VrBow.StringController == hand || VrCrossbow.Claims(hand))
                return;
            if (hand == VrHandSlots.OffController && VrTwoHand.TwoHandable(agent, agent.GetPrimaryWieldedItemIndex()))
                return;

            Mission mission = Mission.Current;
            Vec3 at = VrWeaponHands.HandPoint(hand, state);

            // TEST 475: the off hand under the carried rock steadies it.
            if (hand == VrHandSlots.OffController && VrWeight.Carrying
                && VrSkinnedWeapon.TryGetVisual(HandRole.Main, out MatrixFrame rock, out _, out _, out _)
                && at.Distance(rock.origin) <= VrWeight.SupportM)
            {
                VrWeight.SupportHand = hand;
                _latched[i] = true;
                NativeMethods.bvr_trigger_haptic(i, 0.4f, 0.04f);
                VrLog.Info("TEST 475: the " + hand + " hand is under the rock - steadier.");
                return;
            }

            // TEST 372: a weapon in flight, or lying where it landed, first.
            if (HoldOn && VrThrow.TryCatch(agent, hand, at))
            {
                _latched[i] = true;
                return;
            }

            // TEST 472 / 470: a free hand on a saddle mounts it; mounted, on the
            // lead rope it takes the rope. TEST 511: on a ladder, the ladder.
            // TEST 515: on a ballista or mangonel, the machine - no Use key.
            if (Carried(agent, hand) == EquipmentIndex.None
                && (VrRein.TryMount(agent, hand, at) || VrRein.TryGrab(agent, hand, state, at)
                    || VrLadder.TryGrab(agent, hand, at) || VrSiege.TryUse(agent, hand, at)))
            {
                _latched[i] = true;
                return;
            }

            if (!PickupOn || !mission.IsMainAgentItemInteractionEnabled || agent.IsInWater() || agent.IsUsingGameObject)
                return;

            SpawnedItemEntity item = Nearest(agent, mission.Scene, at, 1f, out float distance);

            if (item == null || distance > ReachM)
            {
                // TEST 474: a siege stone or fire-pot pile is not a ground item.
                if (TryPile(agent, hand, at, out bool taken))
                {
                    _latched[i] = taken;
                    return;
                }

                Miss(hand, at, item, distance);
                return;
            }

            HandRole role = RoleFor(item.WeaponCopy.Item);
            VrHand wanted = role == HandRole.Main ? VrHandSlots.MainController : VrHandSlots.OffController;
            if (wanted != hand)
            {
                VrLog.Info("TEST 332: '" + item.WeaponCopy.Item?.StringId + "' is held in the " + role
                           + " hand (the " + wanted + " controller), not the " + hand + ".");
                NativeMethods.bvr_trigger_haptic(i, 0.3f, 0.03f);
                return;
            }

            _latched[i] = true;

            // TEST 474: a dropped rock or pot comes back the way a pile gives it.
            if (Heavy(item.WeaponCopy))
            {
                Regrab(agent, item, hand, distance);
                return;
            }

            PickUp(agent, item, role, hand, distance);
        }

        /// <summary>TEST 474: a siege rock or fire pot - WeaponClass.Boulder, the
        /// 15 kg items stone and pot piles give.</summary>
        internal static bool Heavy(MissionWeapon weapon)
        {
            return !weapon.IsEmpty && weapon.CurrentUsageItem != null
                   && weapon.CurrentUsageItem.WeaponClass == WeaponClass.Boulder;
        }

        /// <summary>
        /// TEST 474: the grip on a stone or fire-pot pile within reach takes one
        /// straight into the main hand - vanilla's own equip (StonePile.OnTick's
        /// EquipWeaponToExtraSlotAndWield), without its walk to the standing point
        /// or its bend-down animation. Returns whether a pile was in reach.
        /// </summary>
        private static bool TryPile(Agent agent, VrHand hand, Vec3 at, out bool taken)
        {
            taken = false;
            StonePile best = null;
            float bestD = float.MaxValue;

            foreach (MissionObject o in Mission.Current.ActiveMissionObjects)
            {
                if (!(o is StonePile pile) || pile.AmmoCount <= 0
                    || (agent.Team != null && pile.IsDisabledForBattleSide(agent.Team.Side)) || !FreePoint(agent, pile))
                    continue;

                WeakGameEntity e = pile.GameEntity;
                float d = BoxDistance(at, e.GlobalBoxMin, e.GlobalBoxMax);
                if (d < bestD)
                {
                    bestD = d;
                    best = pile;
                }
            }

            if (best == null || bestD > ReachM)
            {
                if (best != null && bestD < 2f && Probe && Stopwatch.GetTimestamp() >= _logAt)
                {
                    _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    VrLog.Info("TEST 474 pile: " + hand + " grip " + F(bestD) + " m from a '" + best.GivenItemID
                               + "' pile (reach " + F(ReachM) + ").");
                }
                return false;
            }

            ItemObject given = MBObjectManager.Instance.GetObject<ItemObject>(best.GivenItemID);
            if (given == null)
            {
                VrLog.Info("TEST 474 pile: '" + best.GivenItemID + "' is not an item - the vanilla pile gives nothing either.");
                return true;
            }

            VrHand wanted = RoleFor(given) == HandRole.Main ? VrHandSlots.MainController : VrHandSlots.OffController;
            if (wanted != hand)
            {
                VrLog.Info("TEST 474 pile: '" + given.StringId + "' is taken with the " + wanted + " hand.");
                NativeMethods.bvr_trigger_haptic((int)hand, 0.3f, 0.03f);
                return true;
            }

            WeakGameEntity pileEntity = best.GameEntity;
            Vec3 min = pileEntity.GlobalBoxMin, max = pileEntity.GlobalBoxMax;
            MatrixFrame top = pileEntity.GetGlobalFrame();
            top.origin = new Vec3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, max.z);

            taken = Take(agent, given, hand, top);
            if (taken)
                best.SetAmmo(best.AmmoCount - 1);

            VrLog.Info("TEST 474 pile: " + hand + " took '" + given.StringId + "' from a pile at " + F(bestD) + " m ("
                       + best.AmmoCount + " left)" + (taken ? "." : " - the engine did not wield it."));
            return true;
        }

        /// <summary>The pile has a pickup point this agent may use (the engine's
        /// own check: not deactivated, not the AI's throwing point, and the hand
        /// not already holding what it gives).</summary>
        private static bool FreePoint(Agent agent, StonePile pile)
        {
            foreach (StandingPoint point in pile.StandingPoints)
            {
                if (point.GameEntity.HasTag(pile.AmmoPickUpTag) && !point.IsDeactivated && !point.IsDisabledForAgent(agent))
                    return true;
            }
            return false;
        }

        /// <summary>A fresh rock or pot into the extra slot, wielded, gliding in
        /// from <paramref name="from"/>.</summary>
        private static bool Take(Agent agent, ItemObject item, VrHand hand, MatrixFrame from)
        {
            var weapon = new MissionWeapon(item, null, null, 1);
            agent.EquipWeaponToExtraSlotAndWield(ref weapon);

            bool inHand = agent.GetPrimaryWieldedItemIndex() == EquipmentIndex.ExtraWeaponSlot;
            if (inHand)
                VrSkinnedWeapon.Glide(HandRole.Main, from);

            NativeMethods.bvr_trigger_haptic((int)hand, 0.8f, 0.1f);
            return inHand;
        }

        /// <summary>TEST 474: a rock or pot on the ground - flagged CannotBePickedUp
        /// by vanilla - taken again: the ground item is retired (the engine removes
        /// it once _readyToBeDeleted) and a fresh one goes into the hand.</summary>
        private static void Regrab(Agent agent, SpawnedItemEntity item, VrHand hand, float distance)
        {
            ItemObject taken = item.WeaponCopy.Item;
            MatrixFrame visual = Visual(item.GameEntity);

            item.GameEntity.SetVisibilityExcludeParents(false);
            ReadyToDelete?.SetValue(item, true);
            PhysicsStopped?.SetValue(item, true);
            item.IsDeactivated = true;

            bool inHand = Take(agent, taken, hand, visual);
            VrLog.Info("TEST 474 regrab: " + hand + " took '" + taken?.StringId + "' off the ground at " + F(distance)
                       + " m" + (inHand ? "." : " - the engine did not wield it."));
        }

        private static void PickUp(Agent agent, SpawnedItemEntity item, HandRole role, VrHand hand, float distance)
        {
            ItemObject taken = item.WeaponCopy.Item;
            MatrixFrame visual = Visual(item.GameEntity);

            EquipmentIndex holding = role == HandRole.Main
                ? agent.GetPrimaryWieldedItemIndex()
                : agent.GetOffhandWieldedItemIndex();

            EquipmentIndex slot = EquipmentIndex.None;
            string why = "vanilla's choice";
            bool vanilla = item.IsStuckMissile() || item.WeaponCopy.IsAnyConsumable()
                           || taken.ItemFlags.HasAnyFlag(ItemFlags.DropOnWeaponChange | ItemFlags.DropOnAnyAction);

            if (!vanilla && holding >= EquipmentIndex.WeaponItemBeginSlot && holding < EquipmentIndex.ExtraWeaponSlot)
            {
                slot = holding;
                why = "replacing the " + role + " hand's '" + agent.Equipment[holding].Item?.StringId + "'";
            }
            else if (!vanilla && AllFull(agent))
            {
                EquipmentIndex last = _lastHeld[(int)role];
                if (last >= EquipmentIndex.WeaponItemBeginSlot && last < EquipmentIndex.ExtraWeaponSlot)
                {
                    slot = last;
                    why = "all slots full; replacing the last held '" + agent.Equipment[last].Item?.StringId + "'";
                }
            }

            // TEST 332c: exactly what SpawnedItemEntity.OnUseStopped does around
            // this call. A ground item that came to rest was moved to batched
            // physics; left batched, the next drop's body simulated while the
            // entity stayed put - "stuck in the air on the second drop" (324d:
            // a constant height at 1-2 m/s for 3 s). And the old ground-item
            // object is retired, or its 180 s lifetime would later Remove() the
            // entity - by then the weapon in the hand.
            item.GameEntity.SetPhysicsMoveToBatched(false);
            agent.OnItemPickup(item, slot, out bool removeWeapon);
            if (removeWeapon)
            {
                ReadyToDelete?.SetValue(item, true);
                PhysicsStopped?.SetValue(item, true);
                item.IsDeactivated = true;
            }

            EquipmentIndex now = role == HandRole.Main
                ? agent.GetPrimaryWieldedItemIndex()
                : agent.GetOffhandWieldedItemIndex();
            bool inHand = now != EquipmentIndex.None && agent.Equipment[now].Item == taken;
            if (inHand)
                VrSkinnedWeapon.Glide(role, visual);

            NativeMethods.bvr_trigger_haptic((int)hand, 0.6f, 0.06f);
            VrLog.Info("TEST 332 pickup: " + hand + " took '" + taken?.StringId + "' from " + F(distance)
                       + " m, slot " + (slot == EquipmentIndex.None ? "auto" : slot.ToString()) + " (" + why + "); "
                       + (inHand ? "in the " + role + " hand, gliding in." : "not wielded (ammo or a slot change)."));
        }

        /// <summary>The nearest pickable weapon to `at` within `radius`, by the
        /// distance from the point to the item's world box (0 inside it), so a
        /// polearm is taken by any part of its length.</summary>
        private static SpawnedItemEntity Nearest(Agent agent, Scene scene, Vec3 at, float radius, out float best)
        {
            best = float.MaxValue;
            SpawnedItemEntity found = null;

            Vec3 min = at - new Vec3(radius, radius, radius);
            Vec3 max = at + new Vec3(radius, radius, radius);

            // Both lists, as MissionItemContourControllerView asks: items still
            // in the physics step and items that have come to rest.
            for (int pass = 0; pass < 2; pass++)
            {
                int n = scene.SelectEntitiesInBoxWithScriptComponent<SpawnedItemEntity>(
                    ref min, ref max, _hits, _ids, pass == 1);

                for (int k = 0; k < n && k < Max; k++)
                {
                    WeakGameEntity e = _hits[k];
                    if (!e.IsValid)
                        continue;

                    SpawnedItemEntity item = e.GetFirstScriptOfType<SpawnedItemEntity>();
                    if (!Usable(agent, item))
                        continue;

                    float d = BoxDistance(at, e.GlobalBoxMin, e.GlobalBoxMax);
                    if (d < best)
                    {
                        best = d;
                        found = item;
                    }
                }
            }

            return found;
        }

        private static bool Usable(Agent agent, SpawnedItemEntity item)
        {
            if (item == null || item.IsRemoved || item.HasUser || item.WeaponCopy.IsEmpty || item.WeaponCopy.Item == null)
                return false;

            // TEST 474: vanilla deactivates a dropped rock or pot (CannotBePickedUp);
            // only a retired one is out of reach.
            if (Heavy(item.WeaponCopy))
                return !(ReadyToDelete?.GetValue(item) is bool retired && retired);

            return !item.IsDeactivated && agent.CanUseObject(item) && agent.CanQuickPickUp(item);
        }

        /// <summary>Distance from a point to an axis-aligned box; 0 inside.</summary>
        internal static float BoxDistance(Vec3 p, Vec3 min, Vec3 max)
        {
            float dx = Math.Max(0f, Math.Max(min.x - p.x, p.x - max.x));
            float dy = Math.Max(0f, Math.Max(min.y - p.y, p.y - max.y));
            float dz = Math.Max(0f, Math.Max(min.z - p.z, p.z - max.z));
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Where the weapon is SEEN: its entity frame times its first
        /// metamesh's frame (a cloth-only lance has none, so the entity alone).</summary>
        private static MatrixFrame Visual(WeakGameEntity e)
        {
            MatrixFrame world = e.GetGlobalFrame();
            if (e.MultiMeshComponentCount > 0)
            {
                MetaMesh meta = e.GetMetaMesh(0);
                if (meta != null)
                    world = world.TransformToParent(meta.Frame);
            }
            return world;
        }

        /// <summary>The hand Bannerlord holds this item in, as VrHandSlots places it.</summary>
        /// <summary>TEST 373c: the VrSkinnedWeapon entity an item is drawn on -
        /// the off one only for a shield; a bow or a crossbow is a main-hand item
        /// whichever wrist carries it, so its glide goes to the main entity.</summary>
        internal static HandRole EntityRole(ItemObject item)
        {
            return item != null && item.ItemType == ItemObject.ItemTypeEnum.Shield ? HandRole.Off : HandRole.Main;
        }

        internal static HandRole RoleFor(ItemObject item)
        {
            if (item == null)
                return HandRole.Main;
            if (item.ItemType == ItemObject.ItemTypeEnum.Shield)
                return HandRole.Off;
            if (VrHandSlots.BowInOffHand
                && (item.ItemType == ItemObject.ItemTypeEnum.Bow || item.ItemType == ItemObject.ItemTypeEnum.Crossbow))
                return HandRole.Off;
            return HandRole.Main;
        }

        private static bool AllFull(Agent agent)
        {
            for (EquipmentIndex s = EquipmentIndex.WeaponItemBeginSlot; s < EquipmentIndex.ExtraWeaponSlot; s++)
            {
                if (agent.Equipment[s].IsEmpty)
                    return false;
            }
            return true;
        }

        private static void Miss(VrHand hand, Vec3 at, SpawnedItemEntity nearest, float distance)
        {
            if (!Probe || Stopwatch.GetTimestamp() < _logAt)
                return;

            _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            VrLog.Info("TEST 332 no pickup: " + hand + " grip at (" + F(at.x) + ", " + F(at.y) + ", " + F(at.z) + "); "
                       + (nearest == null
                           ? "no pickable weapon within 1 m."
                           : "nearest '" + nearest.WeaponCopy.Item?.StringId + "' " + F(distance) + " m (reach " + F(ReachM) + ")."));
        }

        private static void Clear()
        {
            for (int i = 0; i < 2; i++)
                _down[i] = _latched[i] = false;
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
