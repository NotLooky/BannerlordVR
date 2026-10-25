using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BannerlordVR.Interop;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

using MovementControlFlag = TaleWorlds.MountAndBlade.Agent.MovementControlFlag;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 269. Manual archery: grab an arrow from the quiver, nock it on the
    /// bow, pull the string by hand, let go to shoot. No animation, no wind-up.
    ///
    /// WE OWN THE SHOT, THE ENGINE OWNS THE ARROW
    ///
    /// The vanilla draw cannot be the input: its ready phase is timed by an
    /// animation (TEST 246 measured ~430 ms for melee) and it aims from the
    /// camera, not the hands. So while a bow is out the attack bits are masked
    /// (Apply, from the game's own control tick) and the shot is spawned by
    /// Mission.OnAgentShootMissile - the engine's own entry point, the one its
    /// managed code calls itself from Agent.DropItem. It adds the bow's thrust
    /// damage and missile speed exactly as a vanilla shot does, so flight,
    /// gravity, collision, damage, shields (TEST 243), XP and every
    /// MissionBehavior see an ordinary arrow. AddCustomMissile is NOT used: it
    /// passes a damage bonus of 0, and the arrow would hit with its own damage
    /// only.
    ///
    /// THE HANDS
    ///
    /// The bow hand is the wrist the bow is carried on (bow_hand, TEST 231), the
    /// string hand is the other one. Direction is string hand to bow hand, like
    /// Skyrim. The draw is measured in raw stage metres, so world_scale and
    /// hand_scale do not change how far you pull. Power is the pull past the
    /// nock over the full draw, and the missile's speed is the bow's own times
    /// that - the engine scales damage by speed, so a half draw hits softer by
    /// the engine's own rule.
    ///
    /// The trigger is the fingers on the arrow: pressed inside a quiver zone it
    /// takes one, held it keeps it, brought to the bow it nocks, released it
    /// shoots - or, under bow_min_draw_m, puts the arrow back unspent.
    /// </summary>
    public static class VrBow
    {
        public static bool Enabled { get; } = VrConfig.Bool("bow_manual", true);

        // TEST 269f: the quiver is on the back, reached over EITHER shoulder; the
        // jaw anchor of a full draw sits in front of the ears, so a press after a
        // shot cannot take an arrow by accident. TEST 388: the zone is
        // VrHolster.BackZone, in the drawn body's frame from the ears.

        /// <summary>Which end of the arrow mesh is the tip is read off its
        /// bounding box - the end farther from the mesh origin, VrBlade's grip
        /// rule turned round. 1 reverses it for a model authored the other way.</summary>
        private static readonly bool ArrowFlip = VrConfig.Bool("bow_arrow_flip", false);

        /// <summary>String hand to bow grip, stage metres, that nocks the arrow.</summary>
        private static readonly float NockM = VrConfig.Float("bow_nock_m", 0.20f);

        /// <summary>Pull past the nock below which a release puts the arrow back.</summary>
        private static readonly float MinDrawM = VrConfig.Float("bow_min_draw_m", 0.10f);

        /// <summary>Grip to string hand at full draw, stage metres.</summary>
        private static readonly float FullDrawM =
            Math.Max(NockM + 0.05f, VrConfig.Float("bow_full_draw_m", 0.65f));

        /// <summary>TEST 269c: how far the arrow sits above the bow fist's centre,
        /// along the hand's own pinky-to-thumb axis (the grip pose's f), so it
        /// rests on top of the bow hand instead of running through it. The
        /// shot leaves from the same raised line it is drawn on.</summary>
        private static readonly float ArrowUpM = VrConfig.Float("bow_arrow_up_m", 0.05f);

        /// <summary>TEST 269d: where the nock sits in the string hand, in that
        /// grip's own (s, f, u) metres - s into the palm, f pinky to thumb (up
        /// out of the fist), u out of the knuckles. The default is the top front
        /// of the fist, where the thumb meets the index finger - "in the fingers"
        /// rather than 269c's 5 cm above the thumb.</summary>
        internal static readonly Vec3 NockGrip =
            ParseZone(VrConfig.String("bow_nock_grip", "0.00,0.02,0.03")) ?? new Vec3(0f, 0.02f, 0.03f);

        /// <summary>TEST 465: at full draw the arrow's tip stops this far past the
        /// rest (the top of the bow fist) - the draw is never longer than the
        /// arrow. Metres; the string hand is held there too.</summary>
        private static readonly float TipPastM = VrConfig.Float("bow_tip_past_m", 0.06f);

        private static float _arrowLength;
        private static bool _loggedRest;
        private static bool _pinned;
        private static Vec3 _pin;
        private static bool _loggedPin;

        private static readonly bool Probe = VrConfig.Bool("bow_probe", true);

        // Trigger hysteresis: the press is BvrButton.Trigger's 0.7; the release
        // is earlier, so the shot goes on the way out rather than at the stop.
        private const float TriggerDown = 0.7f;
        private const float TriggerUp = 0.4f;

        private const float TickEveryM = 0.05f;

        private const string ReloadSound = "event:/mission/combat/missile/foley/bowreload";
        private const string ReleaseSound = "event:/mission/combat/missile/foley/bowrelease";

        private enum State { Empty, Held, Nocked }

        /// <summary>The hand whose trigger is the quiver, while a bow is out;
        /// null otherwise. VrHolster leaves that hand's shoulder zones alone.</summary>
        internal static VrHand? StringController { get; private set; }

        /// <summary>TEST 487: the string hand has an arrow in it (held or nocked) -
        /// its fingers take the draw hook.</summary>
        internal static bool HoldsArrow => _state != State.Empty;

        private static State _state = State.Empty;
        private static bool _triggerDown;
        private static float _tickedPull;

        private static GameEntity _arrow;
        private static Mission _arrowMission;
        private static MetaMesh _arrowMeta;
        private static string _arrowMeshName;
        private static bool _arrowShown;

        /// <summary>The arrow in its own mesh space: the nock end, and a basis
        /// whose f runs nock to tip. Measured once per mesh.</summary>
        private static MatrixFrame _arrowLocal = MatrixFrame.Identity;

        private static MethodInfo _shoot;
        private static bool _bound;
        private static bool _broken;
        private static bool _loggedOn;
        private static bool _dumpedFirstShot;

        // TEST 269g: the engine's own loaded arrow, taken off while we own the bow.
        private static long _ammoScanAt;
        private static bool _ammoDescribed;
        private static int _ammoRemoved;
        private static readonly List<MetaMesh> _heldAmmo = new List<MetaMesh>();

        /// <summary>
        /// One frame, from AfterMissionTick beside VrMelee. Reads the
        /// controllers, runs the grab / nock / draw / release machine, places the
        /// arrow in hand and fires. The attack mask goes in from Apply.
        /// </summary>
        public static void Tick()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                Mission mission = Mission.Current;
                Agent agent = mission?.MainAgent;

                if (!Owns(agent))
                {
                    Reset("no bow in hand, or a menu, a conversation or no fighting agent");
                    return;
                }

                if (!VrCameraDriver.HasPose
                    || NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                {
                    Reset("no head pose or controller input this frame");
                    return;
                }

                MBAgentVisuals visuals = agent.AgentVisuals;
                Skeleton skeleton = visuals?.GetSkeleton();
                if (skeleton == null || !VrHandSlots.Resolve(agent, skeleton, out VrHandSlot main, out _))
                {
                    Reset("no skeleton to resolve the hands from");
                    return;
                }

                VrHand bowCtl = main.Carrier == HandRole.Off ? VrHandSlots.OffController : VrHandSlots.MainController;
                VrHand stringCtl = bowCtl == VrHand.Left ? VrHand.Right : VrHand.Left;
                StringController = stringCtl;
                BvrHandState bow = bowCtl == VrHand.Left ? input.Left : input.Right;
                BvrHandState str = stringCtl == VrHand.Left ? input.Left : input.Right;

                if (bow.IsActive == 0 || str.IsActive == 0)
                {
                    Reset("a controller lost tracking");
                    return;
                }

                if (!_loggedOn)
                {
                    _loggedOn = true;
                    VrLog.Info("TEST 269: manual archery is on - the " + stringCtl + " trigger takes an "
                               + "arrow from the quiver, nocks it at the bow, and shoots on release. "
                               + "The vanilla draw is masked while a bow is out. bow_manual = 0 "
                               + "gives the bow back to the trigger.");
                }

                MatrixFrame bowWorld = VrCameraDriver.StageToHand(bow.GripOrientation, bow.GripPosition);
                MatrixFrame strWorld = VrCameraDriver.StageToHand(str.GripOrientation, str.GripPosition);
                float draw = (VrMath.ToBannerlord(str.GripPosition) - VrMath.ToBannerlord(bow.GripPosition)).Length;

                // The arrow's line: over the top of the bow fist, and from the
                // string hand's fingers. TEST 499: with five-finger hands, on the
                // DRAWN fist - on top of its index finger, beside the grip (269c's
                // 5 cm up the controller's fist ran through the drawn one), the
                // bow turning about the grip it is held by.
                Vec3 rest = bowWorld.origin + bowWorld.rotation.f.NormalizedCopy() * ArrowUpM;
                Vec3 pivot = bowWorld.origin;
                if (VrHandMesh.TryArrowRest(bowCtl == VrHand.Left ? 0 : 1, out Vec3 restLocal, out Vec3 pivotLocal)
                    && VrSkinnedWeapon.TryHandWorld(HandRole.Main, out MatrixFrame bowHand))
                {
                    rest = bowHand.TransformToParent(restLocal);
                    pivot = bowHand.TransformToParent(pivotLocal);
                    if (!_loggedRest)
                    {
                        _loggedRest = true;
                        Log("TEST 499: the arrow rests on the drawn bow fist - " + F((rest - pivot).Length)
                            + " m from the grip's centre, on top of the index finger; 269c's point was "
                            + F((bowWorld.origin + bowWorld.rotation.f.NormalizedCopy() * ArrowUpM - rest).Length) + " m from it.");
                    }
                }
                Vec3 nockAt = strWorld.origin
                              + strWorld.rotation.s.NormalizedCopy() * NockGrip.x
                              + strWorld.rotation.f.NormalizedCopy() * NockGrip.y
                              + strWorld.rotation.u.NormalizedCopy() * NockGrip.z;

                // TEST 465: nocked, the draw ends where the arrow's tip reaches the
                // bow hand - the nock, the string and the string hand stop there.
                _pinned = false;
                if (_state == State.Nocked)
                {
                    float most = MaxDraw(_arrowLength, TipPastM);
                    Vec3 toNock = nockAt - rest;
                    float length = toNock.Length;
                    if (length > most)
                    {
                        Vec3 stop = rest + toNock * (most / length);
                        _pin = stop - nockAt;
                        _pinned = true;
                        nockAt = stop;

                        if (!_loggedPin)
                        {
                            _loggedPin = true;
                            Log("TEST 465 full draw: the nock stops " + F(most) + " m from the rest (arrow "
                                + F(_arrowLength) + " m, tip " + F(TipPastM) + " m past - bow_tip_past_m); "
                                + "the hand was " + F(length) + " m out.");
                        }
                    }
                }

                bool pressed = str.Trigger >= (_triggerDown ? TriggerUp : TriggerDown);
                bool down = pressed && !_triggerDown;
                _triggerDown = pressed;

                EquipmentIndex bowSlot = agent.GetPrimaryWieldedItemIndex();
                MissionWeapon bowWeapon = agent.Equipment[bowSlot];
                agent.Equipment.GetAmmoCountAndIndexOfType(bowWeapon.Item.Type,
                                                          out int ammo, out EquipmentIndex quiver);

                HideLoadedArrow(agent, visuals, skeleton, bowSlot, quiver);

                switch (_state)
                {
                    case State.Empty:
                        if (down)
                            TryGrab(agent, VrWeaponHands.HandPoint(stringCtl, str), ammo, quiver, stringCtl);
                        break;

                    case State.Held:
                        if (!pressed)
                        {
                            _state = State.Empty;
                            Log("TEST 269 put back: released before the arrow reached the bow.");
                        }
                        else if (draw <= NockM)
                        {
                            _state = State.Nocked;
                            _tickedPull = 0f;
                            VrCombatSound.Play(mission, ReloadSound, bowWorld.origin, agent.Index, -1);
                            Pulse(stringCtl, 0.5f, 20f);
                            Pulse(bowCtl, 0.3f, 15f);
                            Log("TEST 269 nock at " + F(draw) + " m.");
                        }
                        break;

                    case State.Nocked:
                        float pull = Math.Max(0f, draw - NockM);

                        if (!pressed)
                        {
                            _state = State.Empty;
                            if (pull >= MinDrawM)
                                Fire(mission, agent, bowSlot, quiver, ammo, rest, nockAt, pull, bowCtl);
                            else
                                Log("TEST 269 cancel: pulled " + F(pull) + " m, under bow_min_draw_m "
                                    + F(MinDrawM) + " - the arrow goes back unspent.");
                        }
                        else if (pull >= _tickedPull + TickEveryM)
                        {
                            _tickedPull = pull - (pull % TickEveryM);
                            Pulse(stringCtl, 0.1f + 0.3f * Power(pull), 8f);
                        }
                        else if (pull < _tickedPull - TickEveryM)
                        {
                            _tickedPull = pull - (pull % TickEveryM);
                        }
                        break;
                }

                if (_state != State.Nocked)
                    _pinned = false;    // shot or put back: the hand is free again

                // Held, the arrow points along the controller's aim ray, not the
                // grip axis - the grip axis runs through the fist, which is what
                // laid the first build's arrow sideways across the hand.
                Vec3 heldDir = VrCameraDriver.StageToHand(str.AimOrientation, str.AimPosition).rotation.f;
                LoadArrow(mission, agent, quiver);
                PlaceArrow(rest, nockAt, heldDir);

                // TEST 269h: the bow is ours whenever a bow is out - our string,
                // straight at rest; nocked, the bow turns with the arrow and the
                // string runs to the nock, snapping back the frame it leaves.
                VrDrawnBow.Show(mission.Scene, _state == State.Nocked, pivot, rest, nockAt);
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        /// <summary>
        /// The attack bits for this mission tick, cleared while we own the bow,
        /// so the trigger cannot also start the vanilla draw. Called from
        /// CombatControlPatch, after the game has composed the flags.
        /// </summary>
        public static void Apply(Agent agent)
        {
            if (!Enabled || _broken || !Owns(agent))
                return;

            try
            {
                agent.MovementFlags &= ~MovementControlFlag.AttackMask;
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        }

        /// <summary>A bow in hand, a player who can fight, nothing open, and the
        /// engine's shot bound. Mounted is fine: both hands ride the horse, so its
        /// motion cancels out of the draw and the direction.</summary>
        private static bool Owns(Agent agent)
        {
            if (agent == null || !agent.IsActive() || !agent.CombatActionsEnabled)
                return false;

            // TEST 341: a bow still in hand at a siege weapon must not mask the
            // attack bits it fires on.
            if (agent.IsUsingGameObject)
                return false;

            if (!VrSystem.IsActive || !VrMissionPhase.VrDrivesLook || VrMissionPhase.UiMenuOpen || VrMenu.Open)
                return false;

            Mission mission = Mission.Current;
            if (mission == null || mission.Mode == MissionMode.Conversation || mission.Mode == MissionMode.Barter)
                return false;

            MissionWeapon wielded = agent.WieldedWeapon;
            if (wielded.IsEmpty || wielded.CurrentUsageItem == null || !wielded.CurrentUsageItem.IsBow)
                return false;

            return Bind();
        }

        private static bool Bind()
        {
            if (_bound)
                return _shoot != null;

            _bound = true;
            _shoot = AccessTools.Method(typeof(Mission), "OnAgentShootMissile",
                new[] { typeof(Agent), typeof(EquipmentIndex), typeof(Vec3), typeof(Vec3), typeof(Mat3),
                        typeof(bool), typeof(bool), typeof(int) });

            if (_shoot == null)
                VrLog.Warn("TEST 269: Mission.OnAgentShootMissile is not where it was, so manual "
                           + "archery is off and the bow stays on the trigger.");

            return _shoot != null;
        }

        /// <summary>TEST 371b/373: the engine's own shot for any slot - a thrown
        /// weapon is its own missile, a crossbow fires its loaded bolt. False when
        /// the entry point is not there.</summary>
        internal static bool Shoot(Mission mission, Agent agent, EquipmentIndex slot, Vec3 at, Vec3 velocity, Mat3 rot)
        {
            if (!Bind())
                return false;

            _shoot.Invoke(mission, new object[] { agent, slot, at, velocity, rot, false, true, -1 });
            return true;
        }

        /// <summary>
        /// TEST 388: a world point in BODY-local (right, forward, up) metres, from
        /// the drawn head bone - the line of the ears - along the drawn chest's
        /// axes (VrBodyBones.TryBodyFrame). The holsters, the quiver and the bolts
        /// all test this.
        ///
        /// It was head-local: the camera anchor's axes from the eyes. The anchor
        /// is the ROOM's yaw, not the body's - the user's back measured as in
        /// front of them ("javelins found at +0.37 forward") and every reach over
        /// the shoulder missed; a bend or a turned head moved the zones off the
        /// body too.
        /// </summary>
        internal static Vec3 BodyLocal(Vec3 world)
        {
            Agent agent = Mission.Current?.MainAgent;
            if (VrBodyBones.TryBodyFrame(agent, out MatrixFrame body))
                return body.rotation.TransformToLocal(world - body.origin);

            // No skeleton: the old head-local frame, moved back to the ears.
            Vec3 local = VrCameraDriver.Anchor.rotation.TransformToLocal(world - VrCameraDriver.HeadFrame.origin);

            // TEST 269e: the anchor's side axis reads LEFT positive (measured), so
            // it is flipped to right-positive like the body frame.
            local.x = -local.x;
            local.y += 0.10f;
            return local;
        }

        /// <summary>TEST 318: `hand` is the calibrated hand point, the one the
        /// holsters test - the shoulder zones overlap the quiver on purpose.</summary>
        private static void TryGrab(Agent agent, Vec3 hand, int ammo, EquipmentIndex quiver,
                                    VrHand stringCtl)
        {
            Vec3 local = BodyLocal(hand);

            string at = "hand at (" + F(local.x) + ", " + F(local.y) + ", " + F(local.z)
                        + ") body-local from the ears (right, fwd, up)";

            // TEST 388: either shoulder, behind the ears (VrHolster.BackZone).
            if (!VrHolster.BackZone(local))
            {
                Log("TEST 269 no grab: " + at + "; " + VrHolster.BackZoneText + ".");
                return;
            }

            if (ammo <= 0 || quiver == EquipmentIndex.None)
            {
                Log("TEST 269 no grab: the quiver is empty.");
                return;
            }

            _state = State.Held;
            Pulse(stringCtl, 0.3f, 12f);
            Log("TEST 269 grab: " + at + ", " + ammo + " arrow(s) left.");
        }

        /// <summary>
        /// The shot, through the engine's own entry point. The bow's loaded ammo
        /// is what OnAgentShootMissile reads, and it returns silently when that
        /// is empty - so the bow is loaded from the quiver first if it has to be,
        /// and the quiver pays one arrow after.
        /// </summary>
        private static void Fire(Mission mission, Agent agent, EquipmentIndex bowSlot, EquipmentIndex quiver,
                                 int ammo, Vec3 bowAt, Vec3 stringAt, float pull, VrHand bowCtl)
        {
            if (ammo <= 0 || quiver == EquipmentIndex.None)
            {
                Log("TEST 269 no shot: the quiver ran dry while drawing.");
                return;
            }

            Vec3 dir = (bowAt - stringAt).NormalizedCopy();
            MissionWeapon bow = agent.Equipment[bowSlot];
            float baseSpeed = bow.GetModifiedMissileSpeedForCurrentUsage();
            float power = Power(pull);
            float speed = baseSpeed * power;

            bool first = Probe && !_dumpedFirstShot;
            if (first)
                Log("TEST 269 first shot, before: " + Slots(agent, bowSlot, quiver));

            bool loaded = !agent.Equipment[bowSlot].AmmoWeapon.IsEmpty;
            if (!loaded)
                agent.SetReloadAmmoInSlot(bowSlot, quiver, (short)ammo);

            Mat3 rot = Mat3.Identity;
            rot.f = dir;
            rot.Orthonormalize();

            short before = agent.Equipment[quiver].Amount;
            _shoot.Invoke(mission, new object[] { agent, bowSlot, bowAt, dir * speed, rot, false, true, -1 });
            agent.SetWeaponAmountInSlot(quiver, (short)Math.Max(0, before - 1), false);

            VrCombatSound.Play(mission, ReleaseSound, bowAt, agent.Index, -1);
            Pulse(bowCtl, 0.6f + 0.4f * power, 30f);

            if (first)
            {
                _dumpedFirstShot = true;
                Log("TEST 269 first shot, after: " + Slots(agent, bowSlot, quiver));
            }

            Log("TEST 269 shot: draw " + F(pull) + " m past the nock, power " + F(power) + ", speed "
                + F(speed) + " of base " + F(baseSpeed) + ", dir (" + F(dir.x) + ", " + F(dir.y) + ", "
                + F(dir.z) + "), bow was " + (loaded ? "loaded" : "empty - loaded from the quiver")
                + ", quiver " + before + " -> " + agent.Equipment[quiver].Amount + ".");
        }

        /// <summary>
        /// TEST 269g. The engine draws the bow's LOADED arrow itself - the bow
        /// stays loaded under manual archery (first-shot dump: "ammo 1 reload
        /// 1/1") and WeaponData carries an AmmoOffset for it - at the vanilla
        /// animation's hand, which the player sees as a spare arrow standing by
        /// the belt (269f screenshot). Engine-placed meshes on a bone go back to
        /// the animated spot every frame (TEST 226/227), so moving it is no use;
        /// it is taken OFF, the way TEST 229 takes the vanilla weapon off, and
        /// its wrapper held. Matched by the arrow item's own mesh names, on the
        /// four hand bones and the bow's slot entity children only, so the
        /// quiver and anything else on the body are never touched. Twice a
        /// second, because a reload would put it back. The first scan logs every
        /// bone attachment and child, so if it is somewhere else the log says
        /// where.
        /// </summary>
        internal static void HideLoadedArrow(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                            EquipmentIndex bowSlot, EquipmentIndex quiver)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _ammoScanAt)
                return;
            _ammoScanAt = now + Stopwatch.Frequency / 2;

            ItemObject ammo = quiver != EquipmentIndex.None
                ? agent.Equipment[quiver].Item
                : agent.Equipment[bowSlot].AmmoWeapon.Item;
            GameEntity entity = visuals.GetEntity();
            Monster monster = agent.Monster;
            if (ammo == null || entity == null || monster == null)
                return;

            bool describe = !_ammoDescribed;
            _ammoDescribed = true;

            sbyte[] hands =
            {
                monster.MainHandItemBoneIndex, monster.MainHandBoneIndex,
                monster.OffHandItemBoneIndex, monster.OffHandBoneIndex,
            };

            int bones = skeleton.GetBoneCount();
            var onBones = new List<KeyValuePair<sbyte, MetaMesh>>();
            for (sbyte b = 0; b >= 0 && b < bones; b++)
            {
                bool hand = Array.IndexOf(hands, b) >= 0;
                if (!hand && !describe)
                    continue;

                int count = skeleton.GetBoneComponentCount(b);
                for (int i = 0; i < count && i < 16; i++)
                {
                    GameEntityComponent component = skeleton.GetBoneComponentAtIndex(b, i);
                    MetaMesh meta = component as MetaMesh;
                    string name = meta?.GetName() ?? "(" + (component?.GetType().Name ?? "null") + ")";

                    if (describe)
                        Log("TEST 269g bone [" + b + "] " + skeleton.GetBoneName(b) + " carries " + name + ".");

                    if (hand && meta != null && IsArrow(name, ammo))
                        onBones.Add(new KeyValuePair<sbyte, MetaMesh>(b, meta));
                }
            }

            foreach (KeyValuePair<sbyte, MetaMesh> found in onBones)
            {
                entity.RemoveMultiMeshFromSkeletonBone(found.Value, found.Key);
                TookOff(found.Value, "bone [" + found.Key + "] " + skeleton.GetBoneName(found.Key));
            }

            WeakGameEntity bowEntity = agent.GetWeaponEntityFromEquipmentSlot(bowSlot);
            int children = bowEntity.IsValid ? bowEntity.ChildCount : 0;
            for (int c = 0; c < children && c < 8; c++)
            {
                WeakGameEntity child = bowEntity.GetChild(c);
                int metas = child.MultiMeshComponentCount;
                var onChild = new List<MetaMesh>();
                for (int m = 0; m < metas && m < 8; m++)
                {
                    MetaMesh meta = child.GetMetaMesh(m);
                    string name = meta?.GetName() ?? "(null)";
                    if (describe)
                        Log("TEST 269g the bow's slot entity child " + c + " '" + child.Name + "' carries '" + name + "'.");
                    if (meta != null && IsArrow(name, ammo))
                        onChild.Add(meta);
                }

                foreach (MetaMesh meta in onChild)
                {
                    child.RemoveMultiMesh(meta);
                    TookOff(meta, "the bow's slot entity child '" + child.Name + "'");
                }
            }

            if (describe)
            {
                // Not touched, only named: if the arrow is none of the above it is
                // most likely one of these.
                int whole = skeleton.GetComponentCount(GameEntity.ComponentType.MetaMesh);
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < whole && i < 40; i++)
                    names.Append(i == 0 ? "" : ", ")
                         .Append((skeleton.GetComponentAtIndex(GameEntity.ComponentType.MetaMesh, i) as MetaMesh)?.GetName() ?? "?");

                Log("TEST 269g scanned for the engine's loaded arrow ('" + ammo.MultiMeshName + "' / '"
                    + ammo.FlyingMeshName + "'): " + bones + " bones, " + children + " bow child entit(ies); "
                    + "the skeleton itself carries " + whole + " metamesh(es): " + names + ".");
            }
        }

        private static bool IsArrow(string name, ItemObject ammo)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf("quiver", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return (!string.IsNullOrEmpty(ammo.MultiMeshName)
                    && name.StartsWith(ammo.MultiMeshName, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(ammo.FlyingMeshName)
                    && name.StartsWith(ammo.FlyingMeshName, StringComparison.OrdinalIgnoreCase));
        }

        private static void TookOff(MetaMesh meta, string where)
        {
            _heldAmmo.Add(meta);     // wrappers held alive, as TEST 229 does
            _ammoRemoved++;
            if (_ammoRemoved <= 3 || _ammoRemoved % 50 == 0)
                Log("TEST 269g took the engine's loaded arrow '" + meta.GetName() + "' off " + where
                    + " (" + _ammoRemoved + " so far).");
        }

        /// <summary>TEST 465: the longest rest-to-nock line - the arrow less the
        /// tip left past the rest; no limit before the arrow is measured. Pure,
        /// for the tests.</summary>
        internal static float MaxDraw(float arrowLength, float tipPast)
        {
            return arrowLength > 0.2f ? Math.Max(0.1f, arrowLength - tipPast) : float.MaxValue;
        }

        /// <summary>TEST 465: from VrWeaponHands, before the arm solve - at full
        /// draw the string hand is drawn where the string stopped.
        /// ponytail: last tick's stop, so a hand moving at full draw shows a tick
        /// late; compute it in the hand pass if that is ever seen.</summary>
        internal static void AdjustHand(VrHand controller, ref MatrixFrame world)
        {
            if (_pinned && StringController == controller)
                world.origin += _pin;
        }

        private static float Power(float pull)
        {
            return Math.Min(1f, Math.Max(0f, pull / (FullDrawM - NockM)));
        }

        /// <summary>
        /// The arrow you are holding, on an entity of our own (TEST 228: ours
        /// holds its place unpaused; the engine's do not).
        ///
        /// TEST 269b: the item's own arrow mesh, not its flying mesh - the flying
        /// one is a stretched rod for motion at speed, and in the hand it read as
        /// "a long huge stick". Its nock end sits in the string hand either way:
        /// held, it points where the controller points; nocked, it lies along the
        /// string hand to the bow hand, shaft resting on the bow, tip past it by
        /// whatever the draw leaves.
        /// </summary>
        /// <summary>TEST 269h: the arrow model, loaded as soon as a bow is out and
        /// not on the first grab - the bow string is drawn from it, and that is
        /// always shown now.</summary>
        internal static void LoadArrow(Mission mission, Agent agent, EquipmentIndex quiver)
        {
            Scene scene = mission.Scene;
            if (scene == null || quiver == EquipmentIndex.None)
                return;

            // TEST 308: the mission, not the scene pointer - an arena reload can
            // put a new scene at the old address.
            if (_arrow == null || _arrowMission != mission)
            {
                _arrow = GameEntity.CreateEmptyDynamic(scene, false);
                _arrow.SetVisibilityExcludeParents(false);
                _arrowShown = false;
                _arrowMission = mission;
                _arrowMeta = null;
                _arrowMeshName = null;
            }

            ItemObject item = agent.Equipment[quiver].Item;
            string name = item == null ? null
                : !string.IsNullOrEmpty(item.MultiMeshName) ? item.MultiMeshName : item.FlyingMeshName;

            if (name != _arrowMeshName)
            {
                if (_arrowMeta != null)
                    _arrow.RemoveMultiMesh(_arrowMeta);

                _arrowMeshName = name;
                _arrowMeta = string.IsNullOrEmpty(name) ? null : MetaMesh.GetCopy(name, false, true);

                if (_arrowMeta != null)
                {
                    _arrow.AddMultiMesh(_arrowMeta, true);
                    _arrowLocal = FitArrow(name, _arrowMeta, out float arrowLength);
                    _arrowLength = arrowLength;
                    VrDrawnBow.SetCord(name, _arrowLocal, arrowLength);
                }
                else
                {
                    Log("TEST 269 arrow in hand: mesh '" + (name ?? "(none)")
                        + "' did not load - the arrow will be invisible.");
                }
            }
        }

        private static void PlaceArrow(Vec3 rest, Vec3 nockAt, Vec3 heldDir)
        {
            if (_state == State.Empty || _arrow == null || _arrowMeta == null)
            {
                HideArrow();
                return;
            }

            PlaceAt(nockAt, _state == State.Nocked ? (rest - nockAt).NormalizedCopy() : heldDir);
        }

        /// <summary>TEST 373: the loaded arrow model (a bolt, for the crossbow)
        /// with its nock at <paramref name="nockAt"/>, along <paramref name="dir"/>.</summary>
        internal static void PlaceAt(Vec3 nockAt, Vec3 dir)
        {
            if (_arrow == null || _arrowMeta == null)
            {
                HideArrow();
                return;
            }

            MatrixFrame frame = ArrowFrame(nockAt, dir, _arrowLocal);
            _arrow.SetGlobalFrame(in frame, true);
            if (!_arrowShown)
            {
                _arrowShown = true;
                _arrow.SetVisibilityExcludeParents(true);
            }
        }

        /// <summary>The entity frame that puts the mesh's nock (from FitArrow) at
        /// <paramref name="nockAt"/> with its shaft along <paramref name="dir"/>:
        /// undo the mesh's own nock frame, then apply ours. Not local.Inverse():
        /// that is a raw 4x4 inverse that reads the w components, and a Vec3
        /// built in code carries w = -1 - the math test caught it placing the
        /// nock 0.57 m off the hand.</summary>
        internal static MatrixFrame ArrowFrame(Vec3 nockAt, Vec3 dir, MatrixFrame local)
        {
            MatrixFrame nock = MatrixFrame.Identity;
            nock.origin = nockAt;
            nock.rotation.f = dir;
            nock.rotation.Orthonormalize();
            return nock.TransformToParent(local.TransformToLocal(MatrixFrame.Identity));
        }

        /// <summary>
        /// The arrow read out of its bounding box, VrBlade's way: the longest
        /// axis is the shaft, and the tip is the end farther from the mesh
        /// origin (bow_arrow_flip turns it round). Returns the nock end with f
        /// pointing nock to tip, in mesh space.
        /// </summary>
        internal static MatrixFrame FitArrow(string name, MetaMesh meta, out float length)
        {
            length = 0f;
            if (!VrBlade.Bounds(new List<MetaMesh> { meta }, out Vec3 min, out Vec3 max))
            {
                Log("TEST 269b arrow '" + name + "': no bounding box, drawn from its own origin and f.");
                return MatrixFrame.Identity;
            }

            Vec3 size = max - min;
            int axis = 0;
            if (VrBlade.Comp(size, 1) > VrBlade.Comp(size, axis)) axis = 1;
            if (VrBlade.Comp(size, 2) > VrBlade.Comp(size, axis)) axis = 2;

            float lo = VrBlade.Comp(min, axis);
            float hi = VrBlade.Comp(max, axis);
            length = hi - lo;
            bool tipAtMax = Math.Abs(hi) >= Math.Abs(lo);
            if (ArrowFlip)
                tipAtMax = !tipAtMax;

            MatrixFrame local = MatrixFrame.Identity;
            local.origin = VrBlade.SetComp((min + max) * 0.5f, axis, tipAtMax ? lo : hi);
            local.rotation.f = VrBlade.Unit(axis) * (tipAtMax ? 1f : -1f);
            local.rotation.u = VrBlade.Unit((axis + 1) % 3);
            local.rotation.Orthonormalize();

            Log("TEST 269b arrow '" + name + "' measures (" + F(size.x) + ", " + F(size.y) + ", "
                + F(size.z) + ") m: shaft along " + "XYZ"[axis] + ", " + F(hi - lo) + " m, tip at the "
                + (tipAtMax ? "+" : "-") + " end (" + F(tipAtMax ? hi : lo) + "), nock at "
                + F(tipAtMax ? lo : hi) + (ArrowFlip ? " - flipped by bow_arrow_flip." : ". If the "
                + "fletching is at the bow, bow_arrow_flip = 1 turns it round."));

            return local;
        }

        /// <summary>Only while shown, and only on the live scene: this runs every
        /// frame outside a bow, and a native call on an entity whose scene is gone
        /// faults rather than throws.</summary>
        internal static void HideArrow()
        {
            if (_arrow == null || !_arrowShown)
                return;

            _arrowShown = false;
            if (Mission.Current != null && Mission.Current == _arrowMission)
                _arrow.SetVisibilityExcludeParents(false);
        }

        /// <summary>The mission changed: the arrow's entity died with its scene.
        /// Drops the reference without touching it - a new scene can reuse the old
        /// pointer, so the scene check alone cannot tell.</summary>
        public static void Forget()
        {
            _arrow = null;
            _arrowMission = null;
            _arrowMeta = null;
            _arrowMeshName = null;
            _arrowShown = false;
            _state = State.Empty;
            _triggerDown = false;
            _heldAmmo.Clear();
            _ammoDescribed = false;
            VrDrawnBow.Forget();
            VrCrossbow.Forget();
            VrThrow.Forget();
        }

        /// <summary>Drops the arrow. Says why when one was actually in hand -
        /// TEST 269's first session lost a nocked arrow with no line at all.</summary>
        private static void Reset(string why)
        {
            if (_state != State.Empty)
                Log("TEST 269 dropped the " + (_state == State.Nocked ? "nocked" : "held")
                    + " arrow, unspent: " + why + ".");

            _state = State.Empty;
            _triggerDown = false;
            _pinned = false;
            StringController = null;

            // TEST 373: the arrow model and the drawn bow are the crossbow's while
            // it is out; it ticks after this and shows them itself.
            if (VrCrossbow.Active)
                return;

            HideArrow();
            VrDrawnBow.Hide();
        }

        private static void ReportFailure(Exception ex)
        {
            if (_broken)
                return;

            _broken = true;
            try { Reset("manual archery failed"); } catch (Exception) { }
            VrLog.Error("TEST 269: manual archery failed; it is off for this session and the bow "
                        + "goes back to the trigger.", ex);
        }

        private static void Pulse(VrHand controller, float amplitude, float ms)
        {
            NativeMethods.bvr_trigger_haptic((int)controller, amplitude, ms / 1000f);
        }

        internal static Vec3? ParseZone(string text)
        {
            string[] parts = (text ?? "").Split(',');
            if (parts.Length != 3)
                return null;

            float[] v = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return null;

            return new Vec3(v[0], v[1], v[2]);
        }

        private static string Slots(Agent agent, EquipmentIndex bowSlot, EquipmentIndex quiver)
        {
            MissionWeapon b = agent.Equipment[bowSlot];
            MissionWeapon q = agent.Equipment[quiver];
            return "bow slot " + bowSlot + " ammo " + (b.AmmoWeapon.IsEmpty ? "empty" : b.AmmoWeapon.Amount.ToString())
                   + " reload " + b.ReloadPhase + "/" + b.ReloadPhaseCount
                   + "; quiver slot " + quiver + " amount " + q.Amount + "/" + q.MaxAmmo + ".";
        }

        private static void Log(string message)
        {
            if (Probe)
                VrLog.Info(message);
        }

        private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
