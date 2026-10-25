using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

using MovementControlFlag = TaleWorlds.MountAndBlade.Agent.MovementControlFlag;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 373. Manual crossbows - every crossbow - held like a rifle.
    ///
    /// THE HANDS (TEST 373c). The off (left) hand is the crossbow's holder: it
    /// takes it from its holster and holds the front, on the pose tuned for it.
    /// The main (right) hand comes to the start of the stock - the item's own
    /// origin, where the vanilla hand holds it - and its TRIGGER shoots. With
    /// both, the left hand stays put and the stock turns so its start meets the
    /// right hand: aimed from the right hand through the left. The right alone
    /// (the left let go) keeps it as it was and can still shoot; the left alone
    /// cannot. Letting go with both drops it (VrGrab.Let: at its holster it goes
    /// in, anywhere else it flies and comes home).
    ///
    /// LOADING, with the left hand holding and the right one free:
    ///   - grip behind either shoulder (TEST 388): a bolt from the quiver;
    ///   - bring its nock to the seat (the item's own AmmoOffset - the spot the
    ///     engine draws a loaded bolt at, so it is right for every crossbow):
    ///     it is seated;
    ///   - grip at the string and pull it back: it follows the hand, but never
    ///     faster than full draw in xbow_span_s (2 s) - the string is heavy - and
    ///     latches at the nut. Let go early and it snaps forward; the bolt stays.
    /// Either order works; shooting needs both.
    ///
    /// WE OWN THE SHOT, THE ENGINE OWNS THE BOLT, as with the bow (VrBow): the
    /// attack bits are masked, and the shot is Mission.OnAgentShootMissile on the
    /// crossbow's slot, which adds the crossbow's own damage and speed - always
    /// full, the hand does not draw a crossbow. It leaves from the seat along the
    /// stock, exactly where it is seen to point. The engine keeps the crossbow
    /// loaded as it keeps the bow (the shot never consumes it, so no reload
    /// animation ever plays) and the quiver pays one bolt per shot.
    ///
    /// The crossbow is drawn by VrDrawnBow (crossbow mode) at our frame, with
    /// its baked string taken out and a string of ours from the prod tips.
    /// </summary>
    internal static class VrCrossbow
    {
        internal static readonly bool Enabled = VrConfig.Bool("xbow_manual", true);

        private static readonly float SpanS = Math.Max(0.1f, VrConfig.Float("xbow_span_s", 2.0f));
        private static readonly float SeatM = VrConfig.Float("xbow_seat_m", 0.08f);
        private static readonly float StringGrabM = VrConfig.Float("xbow_string_grab_m", 0.08f);
        /// <summary>TEST 375: how far off the stock's body a grip still takes it
        /// (both hands; xbow_stock_m / xbow_fore_m are no longer read).</summary>
        private static readonly float BodyM = VrConfig.Float("xbow_body_m", 0.08f);
        private static readonly float SeatFallbackM = VrConfig.Float("xbow_seat_fallback_m", 0.25f);
        private static readonly bool Probe = VrConfig.Bool("xbow_probe", true);

        /// <summary>TEST 561: a crossbow that cannot be spanned on horseback
        /// (CantReloadOnHorseback - every heavy one) is spanned there anyway, this
        /// many times slower; 0 refuses, as vanilla does. 373 refused, and that is
        /// why only the light crossbows' strings worked in the saddle.</summary>
        private static readonly float MountedSpanMult = VrConfig.Float("xbow_mounted_span_mult", 1.5f);

        /// <summary>TEST 560: the left hand holds the fore-stock palm UP, the stock
        /// lying in the palm channel - built from the drawn hand's own fitted palm
        /// (five-finger hands), not from the tuned class pose. The stock runs from
        /// the heel toward the thumb, turned xbow_palm_diag_deg toward the fingers;
        /// the hand is xbow_palm_back_m behind the prod; xbow_palm_roll_deg turns
        /// the crossbow about its stock if it sits canted.</summary>
        private static readonly bool PalmHold = VrConfig.Bool("xbow_palm_hold", true);
        // Calibrated poses are absolute, as previewed in the menu; the automatic palm hold is only a fallback.
        private static bool UsePalmPose => PalmHold && !VrSkinnedWeapon.FingerPosed(HandRole.Main);
        private static readonly float PalmDiagDeg = VrConfig.Float("xbow_palm_diag_deg", 40f);
        private static readonly float PalmRollDeg = VrConfig.Float("xbow_palm_roll_deg", 0f);
        private static readonly float PalmBackM = VrConfig.Float("xbow_palm_back_m", 0.10f);

        /// <summary>TEST 564: with both hands on, the right aims the stock; the
        /// left hand turns with it by up to this (0 = never, the stock turns in a
        /// still hand and slides out of its fingers).</summary>
        private static readonly float PalmFollowRad = VrConfig.Float("xbow_palm_follow_max_deg", 90f) * DegToRad;

        private const float Down = 0.7f;
        private const float Up = 0.4f;
        private const float PulseEveryS = 0.05f;

        private const string Foley = "event:/mission/combat/missile/foley/";

        private enum Bolt { None, Held, Seated }

        // Per controller (VrHand).
        private static readonly bool[] _hold = new bool[2];
        private static readonly bool[] _gripDown = new bool[2];
        private static bool _triggerDown;

        // The crossbow's own state - kept across a sheathe of the same item.
        private static Bolt _bolt;
        private static float _span;
        private static float _spanS = SpanS;
        private static bool _stringHeld;

        /// <summary>TEST 560: the palm-up hold this tick - the crossbow in the
        /// carrying hand bone's frame (what the fingers close on), and where along
        /// the stock (item z) the hand is.</summary>
        private static MatrixFrame _palmLocal;
        private static bool _havePalm;
        private static float _palmZ;
        private static bool _palmOn;
        private static bool _palmEver;
        private static bool _loggedPalm;

        /// <summary>TEST 564: last tick's turn of the stock off the palm hold,
        /// put on the left hand's target (AdjustHand) and taken back off the drawn
        /// hand before the hold is built again (Palm) - never fed back.</summary>
        private static Vec3 _turnAxis;
        private static float _turnAngle;
        private static Mission _stateMission;
        private static EquipmentIndex _stateSlot = EquipmentIndex.None;
        private static string _stateItem;

        private static bool _owned;
        private static MatrixFrame _mainLocal;
        private static bool _haveMainLocal;

        /// <summary>TEST 373d: where the main hand took the stock, in the item's
        /// own space - grabbed at the trigger, held at the trigger.</summary>
        private static Vec3 _mainInItem;
        private static bool _haveMainInItem;

        /// <summary>TEST 375: the main hand's target moved onto that spot.</summary>
        private static Vec3 _handShift;
        private static bool _haveShift;
        private const float MaxShiftM = 0.30f;
        private static MatrixFrame _mainHandWorld;
        private static Vec3 _mainGripPoint;
        private static Vec3 _mainGripAxis;
        private static bool _haveMainGrip;

        /// <summary>TEST 375: from VrWeaponHands, on each hand's target before the
        /// arm solve - the main hand is drawn on the stock where it took it, not
        /// where the controller slid to.</summary>
        internal static void AdjustHand(HandRole role, ref MatrixFrame world)
        {
            if (role == HandRole.Main)
                _mainHandWorld = world;
            if (!_owned)
                return;

            // TEST 564: the left hand turns with the stock the right hand aims.
            if (role == HandRole.Off)
            {
                if (_turnAngle > 0f)
                    world.rotation = TurnMat(world.rotation, _turnAxis, _turnAngle);
                return;
            }

            // TEST 463: the hand pulling the string is drawn ON it - the string is
            // slower than the hand (xbow_span_s), and the hand ran past it.
            // ponytail: last tick's string, so the holding hand's move shows a tick
            // late; keep it in the crossbow's frame if that is ever seen.
            if (_stringHeld)
                world.origin = _stringAt;
            else if (_haveShift)
                world.origin += _handShift;
        }

        /// <summary>TEST 463: where the string is while the main hand pulls it.</summary>
        private static Vec3 _stringAt;
        private static float _lastTime;
        private static float _pulseAt;
        private static long _logAt;
        private static bool _broken;
        private static bool _loggedOn;
        private static bool _dumpedFirstShot;
        private static string _warnedSeat;

        /// <summary>A crossbow is ours this tick (as of the last one). VrBow
        /// leaves the arrow model and the drawn bow to it.</summary>
        internal static bool Active => _owned;

        /// <summary>The main controller while it is not on the stock: the hand
        /// that loads. VrHolster leaves the back to it.</summary>
        internal static VrHand? LoadController =>
            _owned && !_hold[(int)VrHandSlots.MainController] ? VrHandSlots.MainController : (VrHand?)null;

        /// <summary>Every grip press is the crossbow's while it is out (VrGrab).</summary>
        internal static bool Claims(VrHand hand) => _owned;

        /// <summary>Whether this controller holds the crossbow.</summary>
        internal static bool Holds(VrHand hand) => _owned && _hold[(int)hand];
        internal static bool GripHeld(VrHand hand) => Holds(hand)
            || (_owned && hand == VrHandSlots.MainController && (_stringHeld || _bolt == Bolt.Held));

        internal static bool TryMainGrip(VrHand hand, out Vec3 point, out Vec3 axis, out string itemId)
        {
            point = _mainGripPoint;
            axis = _mainGripAxis;
            itemId = _stateItem;
            return hand == VrHandSlots.MainController && Holds(hand) && _haveMainGrip;
        }

        /// <summary>The main controller's trigger is the crossbow's shot, never
        /// the game's bumper (weapon switch), while that hand holds it.</summary>
        internal static bool TriggerOwned(bool left)
        {
            VrHand hand = left ? VrHand.Left : VrHand.Right;
            return _owned && hand == VrHandSlots.MainController && _hold[(int)hand];
        }

        /// <summary>A crossbow in hand, a player who can fight, nothing open, and
        /// the engine's shot bound - VrBow's gates.</summary>
        internal static bool Owns(Agent agent)
        {
            if (!Enabled || _broken || !VrHolster.Ready(agent))
                return false;

            MissionWeapon wielded = agent.WieldedWeapon;
            return !wielded.IsEmpty && wielded.CurrentUsageItem != null && wielded.CurrentUsageItem.IsCrossBow;
        }

        /// <summary>From AfterMissionTick, after VrBow.</summary>
        internal static void Tick()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                Mission mission = Mission.Current;
                Agent agent = mission?.MainAgent;
                if (!Owns(agent) || !VrCameraDriver.HasPose
                    || NativeMethods.bvr_get_input_state(out BvrInputState input, battleGrip: true) != (int)BvrStatus.Ok)
                {
                    StandDown();
                    return;
                }

                MBAgentVisuals visuals = agent.AgentVisuals;
                Skeleton skeleton = visuals?.GetSkeleton();
                if (skeleton == null
                    || !VrSkinnedWeapon.TryGetVisual(HandRole.Main, out MatrixFrame skinned, out List<MetaMesh> meshes,
                                                     out _, out _)
                    || meshes == null || meshes.Count == 0)
                {
                    StandDown();
                    return;
                }

                VrHand main = VrHandSlots.MainController;
                VrHand off = VrHandSlots.OffController;
                BvrHandState ms = main == VrHand.Left ? input.Left : input.Right;
                BvrHandState os = off == VrHand.Left ? input.Left : input.Right;

                EquipmentIndex slot = agent.GetPrimaryWieldedItemIndex();
                MissionWeapon xbow = agent.Equipment[slot];
                agent.Equipment.GetAmmoCountAndIndexOfType(xbow.Item.Type, out int ammo, out EquipmentIndex quiver);

                if (!_owned)
                    Begin(mission, slot, xbow, main, off, ms, os);
                _owned = true;

                float now = mission.CurrentTime;
                float dt = Math.Max(0f, Math.Min(0.1f, now - _lastTime));
                _lastTime = now;

                // The engine's own loaded bolt off the crossbow, the bolt model
                // (which our string is made of) loaded.
                VrBow.HideLoadedArrow(agent, visuals, skeleton, slot, quiver);
                VrBow.LoadArrow(mission, agent, quiver);

                // Where the crossbow is: in the left hand on its tuned pose (the
                // entity's origin IS the left hand's hold); with the right on the
                // stock too, turned about the left so the stock's start meets the
                // right; carried by the right alone as it was when the left let go.
                // TEST 562: the right hand on the stock carries the crossbow's weight
                // with the left (VrWeight) - the stock aims at its trailing target.
                Vec3 mainPoint = _hold[(int)main] && VrWeight.TryOut(HandRole.Main, out Vec3 weighted)
                    ? weighted
                    : VrWeaponHands.HandPoint(main, ms);
                Vec3 offPoint = VrWeaponHands.HandPoint(off, os);
                MatrixFrame mainCtl = VrCameraDriver.StageToHand(ms.GripOrientation, ms.GripPosition);
                MatrixFrame meshLocal = meshes[0].Frame;
                Vec3 seatLocal = SeatLocal(xbow.Item);

                MatrixFrame entity = skinned;
                Vec3 pivot = skinned.origin;
                _havePalm = _hold[(int)off] && Palm(off, meshLocal, seatLocal, ref entity, ref pivot);
                bool turned = false;
                if (_hold[(int)off])
                {
                    // TEST 373d: the point the right hand took, not the item origin
                    // (the vanilla finger attach, which put the hand on top).
                    Vec3 gripLocal = _haveMainInItem ? _mainInItem : Vec3.Zero;
                    if (_hold[(int)main] && ms.IsActive != 0
                        && AlignTurn(pivot, entity.TransformToParent(meshLocal).TransformToParent(gripLocal), mainPoint,
                                     out Vec3 axis, out float angle))
                    {
                        entity = TurnAbout(entity, pivot, axis, angle);

                        // TEST 564: the left hand turns with it (AdjustHand, next
                        // tick), so the stock stays in the wrap the fingers closed on.
                        if (_havePalm && PalmFollowRad > 0f)
                        {
                            _turnAxis = axis;
                            _turnAngle = Math.Min(angle, PalmFollowRad);
                            turned = true;
                        }
                    }
                }

                if (!turned)
                    _turnAngle = 0f;
                else if (_hold[(int)main] && _haveMainLocal)
                {
                    entity = mainCtl.TransformToParent(_mainLocal);
                }

                MatrixFrame item = entity.TransformToParent(meshLocal);
                Vec3 seat = item.TransformToParent(seatLocal);
                // The left hand's hold: the palm's spot on the stock (TEST 560), or
                // the old pose's entity origin.
                _palmOn |= _havePalm;
                Vec3 fore = _palmOn ? item.TransformToParent(new Vec3(0f, 0f, _palmZ)) : entity.origin;
                Vec3 fwd = item.rotation.u.NormalizedCopy();
                Vec3 rest = VrDrawnBow.TryString(out _, out _, out Vec3 restLocal)
                    ? entity.TransformToParent(restLocal)
                    : seat + fwd * 0.25f;
                Vec3 stringNow = rest + (seat - rest) * _span;

                if (!_loggedOn)
                {
                    _loggedOn = true;
                    VrLog.Info("TEST 373c: manual crossbow is on - the left grip holds it at the front, the right "
                               + "grip takes the start of the stock and its trigger shoots; with the right free, it "
                               + "takes a bolt from behind either shoulder, seats it, and pulls the string (full in "
                               + F(SpanS) + " s). xbow_manual = 0 gives the crossbow back to the game.");
                }

                // --- the main hand: stock, bolt, string ----------------------
                if (ms.IsActive == 0)
                {
                    Follow(main, ms);
                }
                else
                {
                    bool pressed = ms.Trigger >= (_gripDown[(int)main] ? Up : Down);
                    bool edge = pressed && !_gripDown[(int)main];
                    bool released = !pressed && _gripDown[(int)main];
                    _gripDown[(int)main] = pressed;

                    if (edge && !_hold[(int)main])
                        Press(agent, xbow, ammo, quiver, main, mainPoint, item, stringNow, seatLocal);
                    else if (released)
                        ReleaseMain(agent, main, off, mainPoint, slot, entity);

                    if (_bolt == Bolt.Held && pressed)
                    {
                        Vec3 nock = NockAt(ms);
                        if ((nock - seat).Length <= SeatM)
                        {
                            _bolt = Bolt.Seated;
                            Play(mission, agent, xbow, "reload", seat);
                            Pulse(main, 0.5f, 0.03f);
                            Pulse(off, 0.3f, 0.02f);
                            Log("TEST 373 seat: the bolt is in (nock " + F((nock - seat).Length) + " m from the seat).");
                        }
                    }

                    if (_stringHeld)
                        Pull(mission, agent, xbow, main, off, mainPoint, rest, seat, dt, now);
                    if (_stringHeld)
                        _stringAt = rest + (seat - rest) * _span;

                    // The physical trigger (logical Grip under the swap): the shot.
                    bool trig = ms.Grip >= (_triggerDown ? Up : Down);
                    bool shot = trig && !_triggerDown;
                    _triggerDown = trig;
                    if (shot && _hold[(int)main])
                        Fire(mission, agent, slot, xbow, quiver, ammo, seat, fwd, main, off);
                }

                // --- the off hand: the fore-stock ----------------------------
                if (os.IsActive == 0)
                {
                    Follow(off, os);
                }
                else
                {
                    bool pressed = os.Trigger >= (_gripDown[(int)off] ? Up : Down);
                    bool edge = pressed && !_gripDown[(int)off];
                    bool released = !pressed && _gripDown[(int)off];
                    _gripDown[(int)off] = pressed;

                    if (edge && !_hold[(int)off] && _owned)
                    {
                        // TEST 375: anywhere on the stock's body, or at its own hold.
                        float d = Math.Min((offPoint - fore).Length,
                                           OnStock(item.rotation.TransformToLocal(offPoint - item.origin), seatLocal, out _));
                        if (d <= BodyM)
                        {
                            // Back onto its own pose, gliding from where it is seen.
                            _hold[(int)off] = true;
                            _haveMainLocal = false;
                            VrSkinnedWeapon.Glide(HandRole.Main, item);
                            Pulse(off, 0.4f, 0.03f);
                            Log("TEST 373c hold: the off hand took the front back (" + F(d) + " m).");
                        }
                        else
                        {
                            Miss("the off hand is " + F(d) + " m off the crossbow's body (xbow_body_m "
                                 + F(BodyM) + ")");
                        }
                    }
                    else if (released && _hold[(int)off])
                    {
                        _hold[(int)off] = false;
                        if (!_hold[(int)main])
                        {
                            Drop(agent, off, offPoint, slot, entity);
                            return;
                        }

                        // The right hand carries it as it is now, until the left comes back.
                        _mainLocal = mainCtl.TransformToLocal(entity);
                        _haveMainLocal = true;
                        Log("TEST 373c hold: the off hand let go; the main hand holds it alone (its trigger still shoots).");
                    }
                }

                if (!_owned)
                    return;     // dropped by the main hand above

                // TEST 375: the right hand stays on the spot it took. Two hands only
                // fix the stock's direction, so the controller slid along it as the
                // hands moved apart; the arm is drawn to the spot instead (applied
                // to next tick's hand target in VrWeaponHands - the spot moves only
                // as the hands' spacing does, so a tick's age does not show).
                _haveShift = false;
                if (_hold[(int)main] && _hold[(int)off] && _haveMainInItem && ms.IsActive != 0)
                {
                    Vec3 shift = item.TransformToParent(_mainInItem) - mainPoint;
                    if (shift.Length <= MaxShiftM)
                    {
                        _handShift = shift;
                        _haveShift = true;
                    }
                }

                // --- what is seen ---------------------------------------------
                VrDrawnBow.ShowAt(mission.Scene, entity, _span > 0f ? rest + (seat - rest) * _span : (Vec3?)null);

                if (_bolt == Bolt.Seated)
                    VrBow.PlaceAt(seat, fwd);
                else if (_bolt == Bolt.Held)
                    VrBow.PlaceAt(NockAt(ms), VrCameraDriver.StageToHand(ms.AimOrientation, ms.AimPosition).rotation.f);
                else
                    VrBow.HideArrow();
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 373: the manual crossbow failed; it is off for this session and the crossbow "
                            + "goes back to the game.", ex);
                try { StandDown(); } catch (Exception) { }
            }
        }

        /// <summary>The first tick a crossbow is out: the off hand has it (it drew
        /// it, picked it up, or a key put it there), and a crossbow put away loaded
        /// comes out loaded.</summary>
        private static void Begin(Mission mission, EquipmentIndex slot, MissionWeapon xbow, VrHand main, VrHand off,
                                  BvrHandState ms, BvrHandState os)
        {
            string id = xbow.Item?.StringId;
            bool same = ReferenceEquals(_stateMission, mission) && _stateSlot == slot && _stateItem == id;
            bool unload = xbow.CurrentUsageItem.WeaponFlags.HasAnyFlag(WeaponFlags.UnloadWhenSheathed);
            if (!same || unload)
            {
                _bolt = Bolt.None;
                _span = 0f;
            }

            _stateMission = mission;
            _stateSlot = slot;
            _stateItem = id;

            if (_bolt == Bolt.Held)
                _bolt = Bolt.None;
            _stringHeld = false;
            _palmOn = false;
            _turnAngle = 0f;
            _hold[(int)off] = true;
            _hold[(int)main] = false;
            _haveMainLocal = false;
            _haveMainInItem = false;
            _haveMainGrip = false;
            _gripDown[(int)main] = ms.Trigger >= Down;
            _gripDown[(int)off] = os.Trigger >= Down;
            _triggerDown = ms.Grip >= Down;
            _lastTime = mission.CurrentTime;

            Log("TEST 373 out: '" + id + "' (usage '" + xbow.CurrentUsageItem.ItemUsage + "'), bolt " + _bolt
                + ", string " + (_span >= 1f ? "cocked" : "at rest") + ", seat (item space) " + V(SeatLocal(xbow.Item))
                + "; " + Slots(xbow) + ".");
        }

        /// <summary>A main-hand grip press while it is not on the stock: a bolt
        /// from behind the head, the string, or the stock - in that order.</summary>
        private static void Press(Agent agent, MissionWeapon xbow, int ammo, EquipmentIndex quiver, VrHand main,
                                  Vec3 hand, MatrixFrame item, Vec3 stringNow, Vec3 seatLocal)
        {
            Vec3 local = VrBow.BodyLocal(hand);
            bool cocked = _span >= 1f;

            // TEST 388: bolts over EITHER shoulder, behind the ears (the user's ask).
            if (_bolt == Bolt.None && VrHolster.BackZone(local))
            {
                if (ammo <= 0 || quiver == EquipmentIndex.None)
                {
                    Pulse(main, 0.2f, 0.08f);
                    Log("TEST 373 no bolt: the quiver is empty.");
                    return;
                }

                _bolt = Bolt.Held;
                Pulse(main, 0.3f, 0.02f);
                Log("TEST 373 bolt: taken at (" + F(local.x) + ", " + F(local.y) + ", " + F(local.z)
                    + ") body-local from the ears (right, fwd, up); " + ammo + " left.");
                return;
            }

            float toString = (hand - stringNow).Length;
            if (!cocked && toString <= StringGrabM)
            {
                bool saddle = agent.HasMount
                              && xbow.CurrentUsageItem.WeaponFlags.HasAnyFlag(WeaponFlags.CantReloadOnHorseback);
                if (saddle && MountedSpanMult <= 0f)
                {
                    Pulse(main, 0.2f, 0.08f);
                    Log("TEST 373 string refused: '" + xbow.Item?.StringId + "' cannot be spanned on horseback "
                        + "(xbow_mounted_span_mult 0).");
                    return;
                }

                _spanS = saddle ? SpanS * MountedSpanMult : SpanS;
                _stringHeld = true;
                Pulse(main, 0.3f, 0.02f);
                Log("TEST 373 string: taken " + F(toString) + " m from it" + (saddle
                    ? "; TEST 561: a heavy crossbow in the saddle, full in " + F(_spanS) + " s." : "."));
                return;
            }

            // TEST 375: the stock is taken where the hand is ON it - anywhere
            // along its body, within xbow_body_m - and held exactly there.
            Vec3 inItem = item.rotation.TransformToLocal(hand - item.origin);
            float toStock = OnStock(inItem, seatLocal, out float along);
            if (toStock <= BodyM)
            {
                // The left keeps it; the stock turns to keep the spot this hand
                // took (TEST 373d) on the line to it.
                _hold[(int)main] = true;
                _mainInItem = inItem;
                _haveMainInItem = true;
                _mainGripPoint = _mainHandWorld.TransformToLocalNonOrthogonal(
                    item.TransformToParent(new Vec3(0f, seatLocal.y - 0.02f, inItem.z)));
                _mainGripAxis = _mainHandWorld.rotation.TransformToLocal(item.rotation.u).NormalizedCopy();
                _haveMainGrip = _mainGripAxis.LengthSquared > 0.9f;
                Pulse(main, 0.4f, 0.03f);
                Log("TEST 375 hold: the main hand took the stock " + F(along) + " m along it, " + F(toStock)
                    + " m off its line, at " + V(_mainInItem) + " in the item's space (x side, y up, z along) - "
                    + "held there; its trigger shoots.");
                return;
            }

            Miss("main hand at (" + F(local.x) + ", " + F(local.y) + ", " + F(local.z) + ") body-local from the ears; string "
                 + F(toString) + " m (xbow_string_grab_m " + F(StringGrabM) + (cocked ? ", cocked" : "") + "), stock "
                 + F(toStock) + " m off its body (xbow_body_m " + F(BodyM) + "); bolts: " + VrHolster.BackZoneText);
        }

        /// <summary>
        /// TEST 375: how far an item-space point is from the stock's body - the
        /// line along the item's z through the bolt's groove, 2 cm under it, from
        /// the butt to the front of the measured box (the prod across the front is
        /// not a grip). <paramref name="along"/> is where along it the point is.
        /// </summary>
        private static float OnStock(Vec3 inItem, Vec3 seatLocal, out float along)
        {
            float lo = -0.30f, hi = seatLocal.z + 0.40f;
            if (VrDrawnBow.TryBox(out Vec3 min, out Vec3 max))
            {
                lo = min.z;
                hi = max.z;
            }

            return ToSegment(inItem, new Vec3(0f, seatLocal.y - 0.02f, lo), new Vec3(0f, seatLocal.y - 0.02f, hi),
                             out along);
        }

        /// <summary>The distance from <paramref name="p"/> to the segment
        /// <paramref name="a"/>-<paramref name="b"/>, and how far along it the
        /// nearest point is. Pure; the math tests call it.</summary>
        internal static float ToSegment(Vec3 p, Vec3 a, Vec3 b, out float along)
        {
            Vec3 ab = b - a;
            float length = ab.Normalize();
            along = length < 1e-5f ? 0f : Math.Max(0f, Math.Min(length, Vec3.DotProduct(p - a, ab)));
            return (p - (a + ab * along)).Length;
        }

        /// <summary>TEST 560: the crossbow on the left PALM, not on the tuned pose -
        /// <paramref name="entity"/> becomes the entity frame that puts the item
        /// there, <paramref name="pivot"/> the palm channel (Align turns about it).
        /// False without a fitted hand (five-finger hands off): the pose stays.</summary>
        private static bool Palm(VrHand off, MatrixFrame meshLocal, Vec3 seatLocal, ref MatrixFrame entity, ref Vec3 pivot)
        {
            if (!UsePalmPose || !VrSkinnedWeapon.TryHandWorld(HandRole.Main, out MatrixFrame hand)
                || !VrHandMesh.TryPalm(off == VrHand.Left ? 0 : 1, out Vec3 channel, out Vec3 normal,
                                       out Vec3 fingers, out Vec3 thumb))
                return false;

            // TEST 564: the drawn hand carries last tick's turn (AdjustHand); the
            // hold is built from the hand without it, about the palm channel.
            Vec3 palmAt = hand.TransformToParent(channel);
            if (_turnAngle > 0f)
            {
                hand.rotation = TurnMat(hand.rotation, _turnAxis, -_turnAngle);
                hand.origin = palmAt - hand.rotation.TransformToParent(channel);
            }

            // Along the stock: xbow_palm_back_m behind the prod (the string at
            // rest), in front of the bolt's seat, on the stock's box.
            float z = seatLocal.z + 0.12f;
            if (VrDrawnBow.TryString(out _, out _, out Vec3 restEntity))
                z = meshLocal.rotation.TransformToLocal(restEntity - meshLocal.origin).z - PalmBackM;
            if (VrDrawnBow.TryBox(out _, out Vec3 max))
                z = Math.Min(z, max.z - 0.05f);
            z = Math.Max(z, seatLocal.z + 0.02f);
            _palmZ = z;

            MatrixFrame itemWorld = PalmItem(hand, channel, normal, fingers, thumb, z, PalmDiagDeg * DegToRad,
                                             PalmRollDeg * DegToRad);
            // TEST 569: the tool's pose moves it from there, as it moves any weapon.
            _palmLocal = VrSkinnedWeapon.PoseShift(HandRole.Main)
                .TransformToParent(hand.TransformToLocalNonOrthogonal(itemWorld));
            itemWorld = hand.TransformToParent(_palmLocal);
            _palmEver = true;
            pivot = palmAt;
            entity = itemWorld.TransformToParent(meshLocal.TransformToLocal(MatrixFrame.Identity));

            if (!_loggedPalm)
            {
                _loggedPalm = true;
                Log("TEST 560 palm: the left hand holds the stock palm up, " + F(z) + " m along it (seat at "
                    + F(seatLocal.z) + "), turned " + F(PalmDiagDeg) + " deg from across the palm toward the fingers, "
                    + "rolled " + F(PalmRollDeg) + " deg; the crossbow's up is " + F(Degrees(itemWorld.rotation.f, Vec3.Up))
                    + " deg from the world's now. xbow_palm_hold = 0 goes back to the tuned pose.");
            }

            return true;
        }

        private const float DegToRad = (float)(Math.PI / 180.0);

        private static float Degrees(Vec3 a, Vec3 b) => (float)(Math.Acos(Math.Max(-1f, Math.Min(1f,
            Vec3.DotProduct(a.NormalizedCopy(), b.NormalizedCopy())))) * 180.0 / Math.PI);

        /// <summary>
        /// TEST 560: the item frame of a crossbow lying on a palm. The palm's axes
        /// come in <paramref name="hand"/>'s own frame: <paramref name="normal"/>
        /// the way it faces, <paramref name="fingers"/> out along them,
        /// <paramref name="thumb"/> across the palm to the thumb. The stock (item
        /// z) runs across the palm toward the thumb - the muzzle past the web of
        /// thumb and index - turned <paramref name="diag"/> radians toward the
        /// fingers; the crossbow's top (item y) is the way the palm faces, rolled
        /// about the stock by <paramref name="roll"/>; the stock's line passes
        /// through <paramref name="channel"/> (where a handle sits in that hand)
        /// <paramref name="z"/> metres along it. Unit and right-handed whatever
        /// the hand's scale. Pure; the math tests call it.
        /// </summary>
        internal static MatrixFrame PalmItem(MatrixFrame hand, Vec3 channel, Vec3 normal, Vec3 fingers, Vec3 thumb,
                                             float z, float diag, float roll)
        {
            Vec3 n = hand.rotation.TransformToParent(normal).NormalizedCopy();
            Vec3 x = hand.rotation.TransformToParent(fingers).NormalizedCopy();
            Vec3 t = hand.rotation.TransformToParent(thumb).NormalizedCopy();
            Vec3 u = (t * (float)Math.Cos(diag) + x * (float)Math.Sin(diag)).NormalizedCopy();
            Vec3 f = Turn((n - u * Vec3.DotProduct(n, u)).NormalizedCopy(), u, roll);
            return new MatrixFrame(new Mat3(Vec3.CrossProduct(f, u), f, u), hand.TransformToParent(channel) - u * z);
        }

        /// <summary>TEST 560: the crossbow in its carrying hand bone's frame while
        /// the palm holds it - what that hand's fingers close on
        /// (VrSkinnedWeapon.TryHeldLocal), instead of the tuned pose. TEST 564:
        /// from the first tick a crossbow is out (the last hold made) - 560 baked
        /// one wrong grip before the palm's.</summary>
        internal static bool TryPalmLocal(HandRole role, string weaponClass, out MatrixFrame itemInHand)
        {
            itemInHand = _palmLocal;
            return UsePalmPose && _palmEver && role == HandRole.Main && weaponClass == "Crossbow";
        }

        private static void ReleaseMain(Agent agent, VrHand main, VrHand off, Vec3 hand, EquipmentIndex slot,
                                        MatrixFrame entity)
        {
            if (_stringHeld)
            {
                _stringHeld = false;
                if (_span < 1f)
                {
                    Log("TEST 373 string: let go at " + F(_span) + " - it snaps forward; the bolt stays "
                        + (_bolt == Bolt.Seated ? "seated." : "out."));
                    _span = 0f;
                    Play(Mission.Current, agent, agent.Equipment[slot], "release", hand);
                }
                return;
            }

            if (_bolt == Bolt.Held)
            {
                _bolt = Bolt.None;
                Log("TEST 373 bolt: let go before the seat - back in the quiver, unspent.");
                return;
            }

            if (!_hold[(int)main])
                return;

            _hold[(int)main] = false;
            _haveMainGrip = false;
            if (!_hold[(int)off])
            {
                Drop(agent, main, hand, slot, entity);
                return;
            }

            Log("TEST 373c hold: the main hand let go; the off hand holds it - load it now.");
        }

        /// <summary>The string follows the hand back along the stock, never
        /// faster than full draw in xbow_span_s, and latches at the nut.</summary>
        private static void Pull(Mission mission, Agent agent, MissionWeapon xbow, VrHand main, VrHand off, Vec3 hand,
                                 Vec3 rest, Vec3 nut, float dt, float now)
        {
            Vec3 axis = nut - rest;
            float length = axis.Normalize();
            float target = length > 1e-3f ? Math.Max(0f, Math.Min(1f, Vec3.DotProduct(hand - rest, axis) / length)) : 0f;
            _span = Span(_span, target, dt, _spanS);

            // The weight of it: the further the hand runs ahead of the string,
            // the harder it pulls back.
            if (target - _span > 0.03f && now >= _pulseAt)
            {
                _pulseAt = now + PulseEveryS;
                Pulse(main, 0.1f + 0.4f * Math.Min(1f, (target - _span) * 3f), 0.01f);
            }

            if (_span < 1f)
                return;

            _span = 1f;
            _stringHeld = false;
            Play(mission, agent, xbow, "reload", nut);
            Pulse(main, 0.8f, 0.05f);
            Pulse(off, 0.5f, 0.04f);
            Log("TEST 373 cocked: the string latched at the nut.");
        }

        /// <summary>The string's draw after one tick: back toward the hand's
        /// <paramref name="target"/> at most dt/spanS, forward with it at once.
        /// Pure; the math tests call it.</summary>
        internal static float Span(float p, float target, float dt, float spanS)
        {
            return target <= p ? target : Math.Min(target, p + dt / spanS);
        }

        /// <summary>
        /// <paramref name="frame"/> turned about <paramref name="pivot"/> by the
        /// smallest rotation that puts <paramref name="point"/> on the ray from
        /// the pivot through <paramref name="target"/> - the rifle hold: the right
        /// hand stays where it is, the fore-stock aims at the left. Unchanged
        /// with the hands too close to tell a line. Pure; the math tests call it.
        /// </summary>
        internal static MatrixFrame Align(MatrixFrame frame, Vec3 pivot, Vec3 point, Vec3 target)
        {
            return AlignTurn(pivot, point, target, out Vec3 axis, out float angle)
                ? TurnAbout(frame, pivot, axis, angle)
                : frame;
        }

        /// <summary>Align's turn: about <paramref name="axis"/> (unit) by
        /// <paramref name="angle"/> radians (0..pi). False when there is none.</summary>
        internal static bool AlignTurn(Vec3 pivot, Vec3 point, Vec3 target, out Vec3 axis, out float angle)
        {
            axis = Vec3.Zero;
            angle = 0f;
            Vec3 a = point - pivot;
            Vec3 b = target - pivot;
            if (a.Length < 0.05f || b.Length < 0.15f)
                return false;

            a.Normalize();
            b.Normalize();
            axis = Vec3.CrossProduct(a, b);
            float sin = axis.Normalize();
            if (sin < 1e-5f)
                return false;

            angle = (float)Math.Atan2(sin, Vec3.DotProduct(a, b));
            return true;
        }

        private static MatrixFrame TurnAbout(MatrixFrame frame, Vec3 pivot, Vec3 axis, float angle)
        {
            return new MatrixFrame(TurnMat(frame.rotation, axis, angle), pivot + Turn(frame.origin - pivot, axis, angle));
        }

        private static Mat3 TurnMat(Mat3 m, Vec3 axis, float angle)
        {
            return new Mat3(Turn(m.s, axis, angle), Turn(m.f, axis, angle), Turn(m.u, axis, angle));
        }

        private static Vec3 Turn(Vec3 v, Vec3 axis, float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            return v * c + Vec3.CrossProduct(axis, v) * s + axis * (Vec3.DotProduct(axis, v) * (1f - c));
        }

        private static void Fire(Mission mission, Agent agent, EquipmentIndex slot, MissionWeapon xbow,
                                 EquipmentIndex quiver, int ammo, Vec3 seat, Vec3 fwd, VrHand main, VrHand off)
        {
            if (_span < 1f || _bolt != Bolt.Seated)
            {
                Pulse(main, 0.15f, 0.02f);
                Log("TEST 373 click: not ready - string " + (_span >= 1f ? "cocked" : "at " + F(_span))
                    + ", bolt " + _bolt + ".");
                return;
            }

            if (ammo <= 0 || quiver == EquipmentIndex.None)
            {
                Pulse(main, 0.15f, 0.02f);
                Log("TEST 373 click: the quiver ran dry with a bolt seated.");
                return;
            }

            bool first = Probe && !_dumpedFirstShot;
            if (first)
                Log("TEST 373 first shot, before: " + Slots(agent.Equipment[slot]) + "; quiver slot " + quiver + " amount "
                    + agent.Equipment[quiver].Amount + ".");

            bool loaded = !agent.Equipment[slot].AmmoWeapon.IsEmpty;
            if (!loaded)
                agent.SetReloadAmmoInSlot(slot, quiver, (short)ammo);

            float speed = xbow.GetModifiedMissileSpeedForCurrentUsage();
            Mat3 rot = Mat3.Identity;
            rot.f = fwd;
            rot.Orthonormalize();

            short before = agent.Equipment[quiver].Amount;
            if (!VrBow.Shoot(mission, agent, slot, seat, fwd * speed, rot))
            {
                Log("TEST 373 no shot: Mission.OnAgentShootMissile is not bound.");
                return;
            }

            agent.SetWeaponAmountInSlot(quiver, (short)Math.Max(0, before - 1), false);
            _bolt = Bolt.None;
            _span = 0f;

            Play(mission, agent, xbow, "release", seat);
            Pulse(main, 1f, 0.06f);
            Pulse(off, 0.7f, 0.05f);

            if (first)
            {
                _dumpedFirstShot = true;
                Log("TEST 373 first shot, after: " + Slots(agent.Equipment[slot]) + "; quiver amount "
                    + agent.Equipment[quiver].Amount + " (must be exactly one less).");
            }

            Log("TEST 373 shot: speed " + F(speed) + ", dir " + V(fwd) + ", crossbow was "
                + (loaded ? "loaded" : "empty - loaded from the quiver") + ", quiver " + before + " -> "
                + agent.Equipment[quiver].Amount + ".");
        }

        /// <summary>Both hands let go: VrGrab's release - into the holster at
        /// it, else it flies from where it is seen and comes home.</summary>
        private static void Drop(Agent agent, VrHand hand, Vec3 at, EquipmentIndex slot, MatrixFrame entity)
        {
            Log("TEST 373 hold: both hands let go - bolt " + _bolt + ", string "
                + (_span >= 1f ? "cocked" : "at rest") + " (kept for its next draw).");
            StandDown();
            VrGrab.Let(agent, hand, at, slot, entity);
        }

        /// <summary>The item-space seat: the AmmoOffset the engine draws a loaded
        /// bolt at; a crossbow without one (a mod item) gets the stock line.</summary>
        private static Vec3 SeatLocal(ItemObject item)
        {
            Vec3 seat = item?.WeaponComponent?.PrimaryWeapon?.AmmoOffset ?? Vec3.Zero;
            if (seat.x * seat.x + seat.y * seat.y + seat.z * seat.z > 1e-6f)
                return seat;

            if (item != null && _warnedSeat != item.StringId)
            {
                _warnedSeat = item.StringId;
                VrLog.Warn("TEST 373: '" + item.StringId + "' has no AmmoOffset; its bolt seats "
                           + F(SeatFallbackM) + " m up the stock (xbow_seat_fallback_m).");
            }

            return new Vec3(0f, 0f, SeatFallbackM);
        }

        /// <summary>Where the nock is in the right hand - VrBow's own spot.</summary>
        private static Vec3 NockAt(BvrHandState state)
        {
            MatrixFrame grip = VrCameraDriver.StageToHand(state.GripOrientation, state.GripPosition);
            return grip.origin
                   + grip.rotation.s.NormalizedCopy() * VrBow.NockGrip.x
                   + grip.rotation.f.NormalizedCopy() * VrBow.NockGrip.y
                   + grip.rotation.u.NormalizedCopy() * VrBow.NockGrip.z;
        }

        private static void Follow(VrHand hand, BvrHandState state)
        {
            _gripDown[(int)hand] = state.Trigger >= (_gripDown[(int)hand] ? Up : Down);
        }

        /// <summary>The crossbow's foley: light crossbows have their own.</summary>
        private static void Play(Mission mission, Agent agent, MissionWeapon xbow, string what, Vec3 at)
        {
            string usage = xbow.CurrentUsageItem?.ItemUsage ?? "";
            bool light = usage.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0;
            VrCombatSound.Play(mission, Foley + (light ? "lightcrossbow" : "crossbow") + what, at, agent.Index, -1);
        }

        /// <summary>Not ours any more: the crossbow put away, dropped, or a menu.
        /// The bolt and the string stay as they were for the next draw.</summary>
        private static void StandDown()
        {
            if (!_owned)
                return;

            _owned = false;
            _hold[0] = _hold[1] = false;
            _stringHeld = false;
            _haveShift = false;
            _havePalm = false;
            _turnAngle = 0f;
            if (_bolt == Bolt.Held)
                _bolt = Bolt.None;

            VrDrawnBow.Hide();
            VrBow.HideArrow();
        }

        /// <summary>The attack bits cleared while the crossbow is ours, so nothing
        /// starts the vanilla shot or reload. From CombatControlPatch.</summary>
        internal static void Apply(Agent agent)
        {
            if (!Owns(agent))
                return;

            try
            {
                agent.MovementFlags &= ~MovementControlFlag.AttackMask;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 373: masking the crossbow's vanilla shot failed; it is off for this session.", ex);
            }
        }

        /// <summary>The mission changed: the entities died with its scene.</summary>
        internal static void Forget()
        {
            _owned = false;
            _hold[0] = _hold[1] = false;
            _stringHeld = false;
            _haveShift = false;
            _havePalm = false;
            _turnAngle = 0f;
            _bolt = Bolt.None;
            _span = 0f;
            _stateMission = null;
            _stateSlot = EquipmentIndex.None;
            _stateItem = null;
        }

        private static string Slots(MissionWeapon xbow)
        {
            return "crossbow ammo " + (xbow.AmmoWeapon.IsEmpty ? "empty" : xbow.AmmoWeapon.Amount.ToString())
                   + " reload " + xbow.ReloadPhase + "/" + xbow.ReloadPhaseCount;
        }

        private static void Miss(string what)
        {
            if (!Probe || Stopwatch.GetTimestamp() < _logAt)
                return;

            _logAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            VrLog.Info("TEST 373 no grab: " + what + ".");
        }

        private static void Pulse(VrHand hand, float amplitude, float seconds)
        {
            NativeMethods.bvr_trigger_haptic((int)hand, amplitude, seconds);
        }

        private static void Log(string message)
        {
            if (Probe)
                VrLog.Info(message);
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);

        private static string V(Vec3 v) => "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")";
    }
}
