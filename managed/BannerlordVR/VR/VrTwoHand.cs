using System;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 304: a two-handed weapon or a polearm held in BOTH hands, the
    /// Blade &amp; Sorcery way. Hold the off-hand trigger with that hand on the
    /// weapon and it takes hold: the main hand stays the pivot, the blade points
    /// through the off hand, and the off fist stays on the shaft exactly where it
    /// took hold (TEST 304e). Let go of the trigger and the weapon is one-handed
    /// again. Both ways ease over twohand_blend_ms instead of snapping.
    ///
    /// Nothing downstream knows: the main hand's TARGET is turned before the arm
    /// solve, so the wrist, the weapon copy (wrist x ItemLocal, VrSkinnedWeapon),
    /// the blade VrBlade reads and therefore every physical hit follow it.
    ///
    /// The blade's direction in the main hand's frame is latched when the off
    /// hand takes hold - it is rigid for as long as the weapon is the same - from
    /// last tick's target and last tick's blade, which belong to each other.
    /// </summary>
    internal static class VrTwoHand
    {
        private static readonly bool Enabled = VrConfig.Bool("twohand", true);

        /// <summary>How close the off hand must be to the shaft to take hold of
        /// it, in metres. 0.20 (TEST 304b, was 0.15): grabs that worked landed
        /// at 0.11-0.15 on an axe and a lance, so the ones that did not were just
        /// past the edge.</summary>
        private static readonly float GrabM = Math.Max(0.02f, VrConfig.Float("twohand_grab_m", 0.20f));

        /// <summary>Trigger thresholds, with the hysteresis VrBow and VrHolster use.</summary>
        private const float Press = 0.7f;
        private const float Release = 0.4f;

        /// <summary>Closer than this the two hands give no direction.</summary>
        private const float MinSpan = 0.08f;

        /// <summary>TEST 304e: how long taking hold and letting go take, in
        /// seconds - the weapon eases onto the off hand's line and the off hand
        /// onto the shaft, and back, instead of snapping.</summary>
        private static readonly float BlendS =
            Math.Max(0.01f, VrConfig.Float("twohand_blend_ms", 150f) * 0.001f);

        /// <summary>The controller holding the shaft, so VrHolster leaves its
        /// trigger alone; null when nothing is gripped.</summary>
        internal static VrHand? Gripping { get; private set; }

        /// <summary>TEST 304e: in two hands, or still easing into or out of it.</summary>
        internal static bool Active => _blend > 0f;

        /// <summary>TEST 483: fully in two hands - the off hand's hold on the
        /// shaft is latched and no longer easing.</summary>
        internal static bool Settled => _blend >= 1f;

        /// <summary>TEST 487: the shaft as a line in the OFF hand bone's frame,
        /// once the two-handed hold is latched and settled - static for the whole
        /// grip and whatever the slide (it moves the weapon along this line).
        /// offItemLocal: the off hand's finger0 in its bone frame, the target
        /// frame's origin (converted as TryMirror does).</summary>
        internal static bool TryOffLine(Vec3 offItemLocal, out Vec3 point, out Vec3 axis)
        {
            point = axis = Vec3.Zero;
            if (!_latched || !Settled || _item == null)
                return false;

            point = offItemLocal + _offInWeapon.TransformToLocal(_shaftInHand);
            axis = _offInWeapon.rotation.TransformToLocal(_axisInHand).NormalizedCopy();
            return true;
        }

        // TEST 304e: 0 one-handed .. 1 fully in two hands, eased by the tick's
        // own clock; and where the off hand took hold, in the main hand's frame.
        private static float _blend;
        private static long _stamp;
        private static bool _latched;

        /// <summary>Where the off hand holds, in the WEAPON's frame (the main
        /// hand's frame moved by the slide), so a sliding main hand leaves it put.</summary>
        private static MatrixFrame _offInWeapon;

        // TEST 304f: the off fist holds the shaft the way the main fist holds the
        // handle - the main hand's shaft line mirrored across the body through the
        // skeleton's rest pose, into the off hand's target frame.
        private static bool _haveMirror;
        private static Vec3 _offAxis;
        private static Vec3 _offShaft;

        // TEST 304g: the main trigger held while two-handing slides the main hand
        // along the shaft; the weapon stays where it is. _slide is the weapon's
        // offset from the main hand along the shaft, kept until the weapon changes.
        // TEST 304h: the slide's edges. It ran past the off hand - after which
        // the blade aims the other way through it and the weapon flips - and up
        // onto an axe's head. Short of the butt by SlideButtM, of the tip by
        // twohand_slide_tip_m, and of the off hand by twohand_slide_gap_m.
        private const float SlideButtM = 0.05f;
        private static readonly float SlideTipM = VrConfig.Float("twohand_slide_tip_m", 0.25f);

        /// <summary>TEST 374: the slide also needs the main stick held in - under
        /// grip_hold the main grip is down the whole time it holds, so the grip
        /// alone slid the hand on every move along the shaft.</summary>
        private static readonly bool SlideStick = VrConfig.Bool("twohand_slide_stick", true);
        private static readonly float SlideGapM = VrConfig.Float("twohand_slide_gap_m", 0.12f);
        private static bool _atEdge;
        private static float _slide;
        private static bool _slideDown;
        private static bool _sliding;
        private static Vec3 _slideFrom;
        private static float _slideStart;
        private static float _slideMin;
        private static float _slideMax;

        // This tick's main frame fully steered (what the grip is latched in) and
        // as drawn (eased - what the off hand is put on).
        private static MatrixFrame _mainFull;
        private static MatrixFrame _mainDrawn;

        private static bool _down;
        private static bool _missLogged;
        private static string _item;
        private static Vec3 _axisInHand;

        /// <summary>TEST 304c: where the shaft passes the main fist, in the
        /// hand's frame - perpendicular to the axis. The mesh's centre line runs
        /// a few centimetres off the item bone, and that offset turns with the
        /// wrist.</summary>
        private static Vec3 _shaftInHand;
        private static float _side;

        // This tick: the off hand's target, and whether the main hand steered
        // the weapon onto its line.
        private static bool _haveOff;
        private static Vec3 _off;
        private static bool _steered;

        // Last tick's main target, after any steering - the frame the weapon
        // the blade was read from was built on.
        private static bool _haveLast;
        private static MatrixFrame _lastMain;

        /// <summary>Once per tick, before either hand is driven: the trigger,
        /// taking hold and letting go.</summary>
        internal static void Tick(Agent agent, BvrInputState input, VrHandSlot main, VrHandSlot off,
                                  Skeleton skeleton)
        {
            _steered = false;
            _haveOff = false;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float dt = _stamp == 0L
                ? 0f
                : (float)((now - _stamp) / (double)System.Diagnostics.Stopwatch.Frequency);
            _stamp = now;
            dt = Math.Min(dt, 0.1f);

            if (!Enabled || !off.Valid)
            {
                Let("two-handing is off");
                _blend = 0f;
                _latched = false;
                return;
            }

            BvrHandState state = off.Controller == VrHand.Left ? input.Left : input.Right;
            bool pressed = state.IsActive != 0 && state.Trigger >= (_down ? Release : Press);
            bool edge = pressed && !_down;
            _down = pressed;

            if (edge)
                _missLogged = false;

            if (VrWeaponHands.TryTarget(input, off, out MatrixFrame offTarget))
            {
                _haveOff = true;
                _off = offTarget.origin;
            }

            string item = Eligible(agent, main);

            if (Gripping != null)
            {
                if (!pressed || item == null || item != _item || !_haveOff
                    || VrHandCalibration.Capturing)
                {
                    Let(!pressed ? "the trigger let go"
                             : item == null ? "the weapon cannot be held in two hands now"
                             : "the weapon changed");
                }
            }
            else
            {
                TryGrab(agent, main, off, item, pressed, skeleton);
            }

            // TEST 304g: a slid grip belongs to the weapon it was slid on.
            if (item == null || item != _item)
            {
                _slide = 0f;
                _sliding = false;
            }

            Slide(agent, input, main);

            // TEST 304e: ease in while held, out after letting go - unless the
            // weapon itself went away, which has nothing left to ease.
            if (item == null || item != _item || !_haveOff || VrHandCalibration.Capturing)
            {
                _blend = 0f;
            }
            else
            {
                float step = dt / BlendS;
                _blend = Gripping != null ? Math.Min(1f, _blend + step) : Math.Max(0f, _blend - step);
            }

            if (_blend <= 0f && Gripping == null)
                _latched = false;
        }

        /// <summary>Takes hold when the off trigger is held with that hand on
        /// the shaft.</summary>
        private static void TryGrab(Agent agent, VrHandSlot main, VrHandSlot off, string item, bool pressed,
                                    Skeleton skeleton)
        {
            // TEST 304b: while the trigger is HELD, not only on the press - a
            // trigger squeezed on the way to the shaft takes hold when the hand
            // gets there.
            if (!pressed || item == null || !_haveOff || !_haveLast || VrHandCalibration.Capturing
                || VrBow.StringController != null)
                return;

            if (!VrBlade.TryGet(agent, main, out VrBlade.Segment blade))
                return;

            Vec3 grip = blade.GripWorld;
            Vec3 tip = blade.TipWorld;
            Vec3 axis = tip - grip;
            float length = axis.Length;
            if (length < 0.2f)
                return;

            axis = axis * (1f / length);

            // TEST 304b: the SHAFT is the line through the main fist along the
            // weapon - the fist is on it. The mesh box's centre line runs beside
            // the shaft wherever the head is wide (an axe blade, a lance's
            // pennon: the misses sat 0.11-0.15 m off it). Anywhere from the butt
            // to the tip counts - half-swording is a grip.
            //
            // TEST 304d: and the haft runs through the weapon MESH's own origin -
            // crafted pieces stack along the handle from it - not down the
            // middle of the mesh box, which an axe's head pushes 0.1-0.15 m off
            // the haft ('nord_twohanded_axe_1_t3', a 0.29 m wide box: "very
            // misaligned"). The fist is the fallback when there is no copy.
            //
            // TEST 304h: the FIST (last tick's main target), not the weapon's
            // origin - after a slide those are up to the slide apart, and the off
            // hand's side was read off the wrong one: a grab behind the fist read
            // as ahead of it and the blade aimed backwards through the off hand.
            Vec3 hand = _lastMain.origin;
            bool haveMesh = VrSkinnedWeapon.TryMeshWorld(main.Role, out MatrixFrame meshWorld,
                                                         out EquipmentIndex _);
            Vec3 haft = haveMesh ? meshWorld.origin : hand;

            // TEST 460: and ALONG the mesh's own z. The box's long axis is the
            // entity's z, off the haft by the tuned pose's tilt (sturgia_2haxe:
            // pitch -3.1, so 4 cm at 0.8 m - the off hand gripped air near the
            // head and held fine near the main hand). CraftedDataView stacks every
            // piece at num * Vec3.Up, unrotated, so the haft IS the mesh's z.
            float tilt = 0f;
            if (haveMesh)
                axis = HaftAxis(axis, meshWorld.rotation.u, out tilt);

            Vec3 toOff = _off - haft;
            float along = Math.Max(Vec3.DotProduct(grip - haft, axis),
                                   Math.Min(Vec3.DotProduct(tip - haft, axis), Vec3.DotProduct(toOff, axis)));
            float gap = (toOff - axis * along).Length;
            if (gap > GrabM)
            {
                if (!_missLogged)
                {
                    _missLogged = true;
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 304 no grip: the off trigger is held {0:F2} m from '{1}''s shaft "
                        + "(twohand_grab_m {2:F2}) - it takes hold when the hand gets closer.",
                        gap, item, GrabM));
                }

                return;
            }

            _side = Vec3.DotProduct(_off - hand, axis) >= 0f ? 1f : -1f;
            _axisInHand = _lastMain.rotation.TransformToLocal(axis);
            Vec3 haftInHand = _lastMain.rotation.TransformToLocal(haft - _lastMain.origin);
            _shaftInHand = haftInHand - _axisInHand * Vec3.DotProduct(haftInHand, _axisInHand);
            _item = item;
            Gripping = off.Controller;

            // TEST 304e: where the hand lands on the shaft is decided on the next
            // AdjustOff, from this grab's geometry - not kept from an old one.
            _latched = false;

            // TEST 304f: the off fist's own shaft line, mirrored from the main's.
            _haveMirror = TryMirror(agent, skeleton, main, off, out _offAxis, out _offShaft);

            Pulse(off.Controller);
            Pulse(main.Controller);

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 304 grip: the off hand took hold of '{0}' {1:F2} m from the hand, {2} "
                + "({3:F2} m off the weapon's line). The blade now points through it; let go "
                + "of the trigger to release. TEST 460: {4}.",
                item, Vec3.DotProduct(_off - hand, axis),
                _side > 0f ? "toward the tip" : "behind the main hand", gap,
                !haveMesh ? "no mesh, the box's axis"
                    : tilt <= HaftMaxDeg ? "haft = the mesh's z, " + tilt.ToString("F1", CultureInfo.InvariantCulture)
                                           + " deg off the box"
                    : "the mesh's z is " + tilt.ToString("F0", CultureInfo.InvariantCulture)
                      + " deg off the box - not a haft, the box's axis kept"));
        }

        /// <summary>TEST 460: past this the mesh is not authored along its z.</summary>
        private const float HaftMaxDeg = 20f;

        /// <summary>TEST 460: the haft's direction - the mesh's own z, signed like
        /// the box's axis, when the two are within HaftMaxDeg; else the box's.
        /// Pure, for the tests.</summary>
        private static Vec3 HaftAxis(Vec3 box, Vec3 meshUp, out float deg)
        {
            Vec3 up = meshUp.NormalizedCopy();
            float cos = Vec3.DotProduct(up, box);
            deg = (float)(Math.Acos(Math.Min(1f, Math.Abs(cos))) * (180.0 / Math.PI));
            if (deg > HaftMaxDeg)
                return box;

            return cos >= 0f ? up : up * -1f;
        }

        /// <summary>The main hand's target, before the arm solve: turned by the
        /// shortest arc that puts the blade through the off hand, which keeps the
        /// controller's own roll about the blade - the edge stays where the wrist
        /// puts it.</summary>
        internal static void AdjustMain(ref MatrixFrame world)
        {
            if (_blend > 0f && _haveOff)
            {
                // TEST 304c: the SHAFT, not the fist's centre, runs through the
                // off hand. Aiming from the fist left the shaft a few centimetres
                // to one side, and as the main wrist rolled that side went round
                // and walked the shaft out of the off palm. Two passes, because
                // the offset turns with the aim it decides.
                Mat3 rotation = Steer(world.rotation, world.origin, _shaftInHand, _axisInHand,
                                      _off, _side, out _steered);

                if (_steered)
                {
                    _mainFull = world;
                    _mainFull.rotation = rotation;

                    // TEST 304e: eased from the hand's own aim onto the off
                    // hand's line, and back after letting go.
                    float t = Ease(_blend);
                    world.rotation = t >= 1f ? rotation : VrWeight.Blend(world.rotation, rotation, t);
                    _mainDrawn = world;
                }
            }

            _lastMain = world;
            _haveLast = true;
        }

        /// <summary>TEST 316: VrCollide moved the main hand off a shield or a body.
        /// The weapon is drawn there, so the off fist holds it there.</summary>
        internal static void Redrawn(MatrixFrame world)
        {
            if (_steered)
                _mainDrawn = world;

            _lastMain = world;
        }

        /// <summary>The main hand's rotation that runs its shaft (offset
        /// `shaftInHand` from the fist, along `axisInHand`) through `off`. Pure,
        /// for the tests.</summary>
        private static Mat3 Steer(Mat3 rotation, Vec3 origin, Vec3 shaftInHand, Vec3 axisInHand,
                                  Vec3 off, float side, out bool steered)
        {
            steered = false;
            for (int pass = 0; pass < 2; pass++)
            {
                Vec3 span = off - (origin + rotation.TransformToParent(shaftInHand));
                float length = span.Length;
                if (length < MinSpan)
                    break;

                rotation = VrArmIk.Aim(rotation, axisInHand, span * (side / length));
                steered = true;
            }

            return rotation;
        }

        /// <summary>The off hand's target: where it took hold of the shaft, fist
        /// along it - held there, in the weapon's own frame, until the trigger
        /// lets go (TEST 304e), and eased on and off. Only while the main hand
        /// steered the weapon this tick.</summary>
        internal static void AdjustOff(ref MatrixFrame world, MatrixFrame controller)
        {
            if (!_steered)
                return;

            if (!_latched)
            {
                // Easing out of a grip that never landed: nothing to ease from.
                if (Gripping == null)
                    return;

                Vec3 pivot = _mainFull.origin + _mainFull.rotation.TransformToParent(_shaftInHand);
                Vec3 want = _mainFull.rotation.TransformToParent(_axisInHand).NormalizedCopy();

                MatrixFrame grip = world;
                if (_haveMirror)
                {
                    // TEST 304f: the fist's own line onto the shaft - turned by the
                    // shortest arc (the wrist keeps its roll round the shaft, which
                    // a closed fist can have any of), then moved so that line IS the
                    // shaft. Nearer way along it is the way it holds.
                    Vec3 now = world.rotation.TransformToParent(_offAxis);
                    Vec3 along = Vec3.DotProduct(now, want) >= 0f ? want : want * -1f;
                    grip.rotation = VrArmIk.Aim(world.rotation, _offAxis, along);

                    Vec3 palm = world.origin + grip.rotation.TransformToParent(_offShaft);
                    grip.origin = world.origin + (pivot + want * Vec3.DotProduct(palm - pivot, want) - palm);
                }
                else
                {
                    grip.origin = pivot + want * Vec3.DotProduct(world.origin - pivot, want);

                    // The raw controller's forward runs through the closed fist (pinky to
                    // thumb); whichever way along the shaft is nearer is the way it holds.
                    Vec3 fist = controller.rotation.f.NormalizedCopy();
                    Vec3 along = Vec3.DotProduct(fist, want) >= 0f ? want : want * -1f;
                    grip.rotation = VrArmIk.Aim(grip.rotation, grip.rotation.TransformToLocal(fist), along);
                }

                _offInWeapon = Weapon(_mainFull).TransformToLocal(grip);
                _latched = true;

                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 304e grip: the off hand holds '{0}' {1:F2} m along the shaft from the main "
                    + "fist ({2}) and stays there until the trigger lets go.",
                    _item ?? "?", Vec3.DotProduct(grip.origin - _mainFull.origin, want),
                    _haveMirror ? "fist line mirrored from the main hand - TEST 304f" : "controller's fist axis"));
            }

            MatrixFrame held = Weapon(_mainDrawn).TransformToParent(_offInWeapon);
            float t = Ease(_blend);
            world.origin = world.origin + (held.origin - world.origin) * t;
            world.rotation = VrWeight.Blend(world.rotation, held.rotation, t);
        }

        /// <summary>The weapon's frame: the main hand's, moved along the shaft by
        /// the slide (TEST 304g).</summary>
        private static MatrixFrame Weapon(MatrixFrame main)
        {
            main.origin = main.origin + main.rotation.TransformToParent(_axisInHand) * _slide;
            return main;
        }

        /// <summary>TEST 304g: the weapon's offset from the main hand along the
        /// shaft, for VrSkinnedWeapon - zero for anything but the main hand's
        /// two-handable weapon that was slid.</summary>
        internal static Vec3 SlideOffset(HandRole role, MatrixFrame handWorld)
        {
            if (role != HandRole.Main || _slide == 0f || _item == null)
                return Vec3.Zero;

            return handWorld.rotation.TransformToParent(_axisInHand).NormalizedCopy() * _slide;
        }

        /// <summary>TEST 304g: the main trigger, held while two-handing, slides the
        /// main hand along the shaft. The weapon keeps its place (the hand's move
        /// along the shaft is taken back off the weapon), stops 5 cm short of
        /// either end, and the hand stays where it was let go.</summary>
        private static void Slide(Agent agent, BvrInputState input, VrHandSlot main)
        {
            BvrHandState state = main.Controller == VrHand.Left ? input.Left : input.Right;
            // Only once the off hand has landed (its place is one of the edges).
            // TEST 374: with the main stick held in as well.
            bool pressed = Gripping != null && _haveLast && _latched && state.IsActive != 0
                           && state.Trigger >= (_slideDown ? Release : Press)
                           && (!SlideStick || (state.Buttons & (int)BvrButton.ThumbClick) != 0);
            _slideDown = pressed;

            if (!pressed)
            {
                if (_sliding)
                {
                    _sliding = false;
                    Pulse(main.Controller);
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "TEST 304g slide: the main hand stays {0:F2} m {1} along '{2}' from where it took it.",
                        Math.Abs(_slide), _slide <= 0f ? "toward the tip" : "toward the butt", _item ?? "?"));
                }

                return;
            }

            if (!VrWeaponHands.TryTarget(input, main, out MatrixFrame target))
                return;

            Vec3 axis = _lastMain.rotation.TransformToParent(_axisInHand).NormalizedCopy();

            if (!_sliding)
            {
                if (!VrBlade.TryGet(agent, main, out VrBlade.Segment blade))
                    return;

                // The weapon's two ends, measured from the hand along the shaft
                // (the axis points at the tip).
                Vec3 hand = _lastMain.origin;
                float a = Vec3.DotProduct(blade.GripWorld - hand, axis);
                float b = Vec3.DotProduct(blade.TipWorld - hand, axis);

                _slideFrom = Body(target.origin);
                _slideStart = _slide;
                SlideBounds(_slideStart, Math.Min(a, b), Math.Max(a, b),
                            Vec3.DotProduct(_offInWeapon.origin, _axisInHand), _side,
                            out _slideMin, out _slideMax);

                _sliding = true;
                _atEdge = false;
                Pulse(main.Controller);
            }

            // TEST 304i: the hand's move against the BODY - in the world, walking or
            // strafing with a slanted shaft read as sliding along it.
            Vec3 moved = VrCameraDriver.Anchor.rotation.TransformToParent(Body(target.origin) - _slideFrom);
            float wanted = _slideStart - Vec3.DotProduct(moved, axis);
            _slide = Math.Max(_slideMin, Math.Min(_slideMax, wanted));

            // A knock on reaching an edge, once.
            bool atEdge = wanted < _slideMin || wanted > _slideMax;
            if (atEdge && !_atEdge)
                Pulse(main.Controller);
            _atEdge = atEdge;
        }

        /// <summary>TEST 374: the main stick's click is the slide while the off
        /// hand holds the shaft, so the game does not also get R3.</summary>
        internal static bool ThumbOwned(bool left)
        {
            return SlideStick && Gripping != null
                   && (left ? VrHand.Left : VrHand.Right) == VrHandSlots.MainController;
        }

        /// <summary>A world point in the camera anchor's frame - the body's, the
        /// one the controllers are carried in.</summary>
        private static Vec3 Body(Vec3 world)
        {
            MatrixFrame anchor = VrCameraDriver.Anchor;
            return anchor.rotation.TransformToLocal(world - anchor.origin);
        }

        /// <summary>TEST 304h: the slide's range. The hand sits at -slide along the
        /// shaft in the weapon's frame (toward the tip lowers the slide); `butt` and
        /// `tip` are the weapon's ends and `offAt` the off hand, in the weapon's
        /// frame along the shaft, at `start`. Short of the butt, the tip and the off
        /// hand; a grip already past an edge stays where it is rather than jump.
        /// Pure, for the tests.</summary>
        private static void SlideBounds(float start, float butt, float tip, float offAt, float side,
                                        out float min, out float max)
        {
            // butt/tip were measured from the hand at -start; in the weapon frame
            // they are fixed, and so is offAt.
            min = start - (tip - SlideTipM);
            max = start - (butt + SlideButtM);

            if (side > 0f)
                min = Math.Max(min, SlideGapM - offAt);
            else
                max = Math.Min(max, -offAt - SlideGapM);

            min = Math.Min(min, start);
            max = Math.Max(max, start);
        }

        /// <summary>TEST 304f: the main hand's shaft line (`_axisInHand`, through
        /// `_shaftInHand`, in its target frame - origin on the item bone) mirrored
        /// across the body into the off hand's: main-hand local -> entity through
        /// the main hand's REST frame, x (the body's side) negated, -> off-hand local
        /// through the off hand's rest frame. A closed fist is a closed fist on
        /// either side, and the main one is the grip the user tuned.</summary>
        private static bool TryMirror(Agent agent, Skeleton skeleton, VrHandSlot main, VrHandSlot off,
                                      out Vec3 axis, out Vec3 shaft)
        {
            axis = Vec3.Zero;
            shaft = Vec3.Zero;

            try
            {
                MBAgentVisuals visuals = agent.AgentVisuals;
                if (visuals == null || skeleton == null
                    || !VrArmBones.TryChainForHand(agent, visuals, skeleton, main.Bone, out VrArmChain mainChain)
                    || !VrArmBones.TryChainForHand(agent, visuals, skeleton, off.Bone, out VrArmChain offChain))
                    return false;

                Mat3 mainRest = Axes(skeleton.GetBoneEntitialRestFrame(main.Bone, false).rotation);
                Mat3 offRest = Axes(skeleton.GetBoneEntitialRestFrame(off.Bone, false).rotation);

                axis = Mirror(mainRest, offRest, _axisInHand).NormalizedCopy();
                shaft = Mirror(mainRest, offRest, mainChain.ItemLocal + _shaftInHand) - offChain.ItemLocal;

                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 304f grip: main fist line axis ({0:F2}, {1:F2}, {2:F2}) through ({3:F3}, {4:F3}, {5:F3}) "
                    + "-> off fist axis ({6:F2}, {7:F2}, {8:F2}) through ({9:F3}, {10:F3}, {11:F3}).",
                    _axisInHand.x, _axisInHand.y, _axisInHand.z, _shaftInHand.x, _shaftInHand.y, _shaftInHand.z,
                    axis.x, axis.y, axis.z, shaft.x, shaft.y, shaft.z));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Main-hand local -> off-hand local, mirrored across the body.
        /// Pure, for the tests.</summary>
        private static Vec3 Mirror(Mat3 mainRest, Mat3 offRest, Vec3 v)
        {
            Vec3 e = mainRest.TransformToParent(v);
            e.x = -e.x;
            return offRest.TransformToLocal(e);
        }

        /// <summary>Each axis made unit length, its direction - and so the frame's
        /// handedness - kept (Orthonormalize would rebuild one from the other two).</summary>
        private static Mat3 Axes(Mat3 m)
        {
            m.s = m.s.NormalizedCopy();
            m.f = m.f.NormalizedCopy();
            m.u = m.u.NormalizedCopy();
            return m;
        }

        /// <summary>Smoothstep: starts and lands without a jolt.</summary>
        private static float Ease(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return t * t * (3f - 2f * t);
        }

        /// <summary>The held item's id when the main hand holds a two-handed
        /// weapon or a polearm and the off hand is empty; null otherwise.</summary>
        private static string Eligible(Agent agent, VrHandSlot main)
        {
            if (!main.Valid || !TwoHandable(agent, main.Equipment))
                return null;

            return agent.Equipment[main.Equipment].Item?.StringId ?? "?";
        }

        /// <summary>Whether the weapon in this slot can take the off hand on its
        /// shaft right now. VrGrab asks it so a shaft grab beats a pickup.</summary>
        internal static bool TwoHandable(Agent agent, EquipmentIndex slot)
        {
            if (slot < EquipmentIndex.WeaponItemBeginSlot || slot >= EquipmentIndex.ExtraWeaponSlot)
                return false;

            MissionWeapon weapon = agent.Equipment[slot];
            WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
            if (usage == null || !usage.IsMeleeWeapon || usage.IsShield
                || !(usage.IsTwoHanded || usage.IsPolearm))
                return false;

            return agent.WieldedOffhandWeapon.IsEmpty;
        }

        private static void Let(string why)
        {
            if (Gripping == null)
                return;

            VrHand controller = Gripping.Value;
            Gripping = null;
            _steered = false;
            Pulse(controller);

            VrLog.Info("TEST 304 grip: the off hand let go of '" + (_item ?? "?") + "' - " + why + ".");
        }

        private static void Pulse(VrHand controller)
        {
            try
            {
                NativeMethods.bvr_trigger_haptic((int)controller, 0.4f, 0.04f);
            }
            catch (Exception)
            {
                // A pulse is never worth a throw in the hand pass.
            }
        }
    }
}
