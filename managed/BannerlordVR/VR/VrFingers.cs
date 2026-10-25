using System;
using System.Globalization;
using BannerlordVR.Interop;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 351/480. The hand's one finger bone, l_finger0/r_finger0.
    ///
    /// The player skeleton has 28 bones and no finger bones: each hand has one
    /// finger0, the item bone a weapon hangs from, and the hand mesh's four
    /// fingers follow it as one block.
    ///
    /// TEST 480: written AFTER the weapons are placed (VrWeaponHands, right after
    /// DriveWeapons), so VrSkinnedWeapon still reads the animation's finger0 and
    /// every grip, freeze and tuned pose keeps its base.
    ///  * a hand that holds something shows the grip its weapon is placed with
    ///    (fixed or frozen) - the locomotion animation no longer opens and
    ///    closes it ("moving right closes the right hand"). Until the grip
    ///    freezes, the engine's own pose stays.
    ///  * an empty hand closes with the physical grip: open = finger0's rest
    ///    pose, closed = that hand's last held grip, else rest turned
    ///    finger_close_deg about finger_axis. (351's largest-turn-ever latch fed
    ///    strafe poses into "closed"; it is gone.)
    /// The bone's position is never changed - the arm IK's wrist-to-grip offset
    /// is it.
    ///
    /// TEST 482/487: the curl model for VrHandMesh's five fingers - what each
    /// hand does (Grips), each digit's curl, and while holding, how far the
    /// index and thumb are closed onto the handle (Close), from the PHYSICAL
    /// controls and the touch sensors (VrFingerRig.FreeCurls / Engaged).
    /// </summary>
    internal static class VrFingers
    {
        private static readonly bool Enabled = VrConfig.Bool("finger_curl", true);
        private static readonly float CloseDeg = VrConfig.Float("finger_close_deg", 60f);
        private static readonly Vec3 Axis = ParseAxis(VrConfig.String("finger_axis", "y"));
        private static readonly bool Swapped = VrConfig.Bool("controls_swap_trigger_grip", false);
        private static readonly float HoldCurl = VrConfig.Float("finger_hold_curl", 0.75f);
        private static readonly float ThumbHold = VrConfig.Float("finger_thumb_hold", 0.6f);
        /// <summary>TEST 494: the curls follow their targets on a damped spring
        /// (omega = 1 / finger_smooth_ms) - an S-curve, so the keyframes change at
        /// an even pace. 482's exponential ease jumped 1.5 keys in its first frame
        /// and crept through the last ones a key every few frames: stop-motion.</summary>
        private static readonly float Omega = 1000f / Math.Max(1f, VrConfig.Float("finger_smooth_ms", 40f));
        private static readonly float Damp = Math.Max(0.1f, VrConfig.Float("finger_smooth_damping", 0.9f));
        private static readonly float IndexTouch = VrConfig.Float("finger_index_touch", 0.25f);
        private static readonly float ThumbTouch = VrConfig.Float("finger_thumb_touch", 0.6f);
        private static readonly float[] Hook = VrConfig.Floats("finger_hook", new[] { 0.35f, 0.55f, 0.6f });

        /// <summary>TEST 494: the most the thumb's curl is ever asked for outside a
        /// baked hold - VrHandMesh's thumb keys span 0 .. this, not 0 .. 1 (a free
        /// thumb never went past the stick's 0.6, so the top key was never shown).</summary>
        internal static readonly float ThumbSpan = Math.Max(0.1f, Math.Min(1f, Math.Max(ThumbTouch, Math.Max(ThumbHold, Hook[0]))));

        /// <summary>TEST 487: what a hand's fingers do.
        ///  * Free: the controls (FreeCurls);
        ///  * Held: close on what VrSkinnedWeapon places in it (HeldRole);
        ///  * TwoHand: the off hand on the main weapon's shaft (VrTwoHand);
        ///  * Curl: holding, but with no fixed relation to fit (the crossbow's
        ///    stock in the main hand turns with the controller) - finger_hold_curl;
        ///  * Hook: the bow's string hand with an arrow - the draw hook
        ///    (finger_hook = thumb, index, middle/ring/pinky);
        ///  * Fist: every finger closed - a hand on a ladder rung or the ballista's
        ///    ring (TEST 520: "when gripping to climb close all fingers").</summary>
        internal enum Grip { Free, Held, TwoHand, Curl, Hook, Fist }

        /// <summary>TEST 481: each digit's curl, 0 relaxed .. 1 fist, per hand
        /// [0 left, 1 right][thumb, index, middle, ring, pinky] - VrHandMesh
        /// shows the copy nearest it. Free: the controls (TEST 482); holding:
        /// finger_hold_curl / finger_thumb_hold until the hold copies are built;
        /// Hook: finger_hook. Eased over finger_smooth_ms.</summary>
        internal static readonly float[,] Curl = new float[2, VrFingerRig.Digits];

        /// <summary>TEST 487, per hand: what it does, which VrSkinnedWeapon role
        /// it holds (a bow is the main item on the off hand; two-handing, the off
        /// hand holds the main weapon), whether the index and thumb are ON the
        /// handle (TEST 482's touch), and how far each is closed onto it -
        /// [side, 0 thumb / 1 index], 0 relaxed .. 1 wrapped, eased.</summary>
        internal static readonly Grip[] Grips = new Grip[2];
        internal static readonly HandRole[] HeldRole = new HandRole[2];
        internal static readonly bool[] IndexEngaged = { true, true };
        internal static readonly bool[] ThumbEngaged = { true, true };
        internal static readonly float[,] Close = new float[2, 2];

        internal static bool Holding(int side) => Grips[side] == Grip.Held || Grips[side] == Grip.TwoHand || Grips[side] == Grip.Curl;

        private static long _curlAt;
        private static readonly Vec3[] _curl = new Vec3[2], _curlVel = new Vec3[2], _close = new Vec3[2], _closeVel = new Vec3[2];
        private static bool _noTouch;
        private static readonly uint[] _bound = { 0xFF, 0xFF };

        private sealed class Side
        {
            public UIntPtr Skeleton;
            public sbyte Hand = -1, Item = -1;
            public Quaternion Rest;
            public Quaternion Closed;
            public bool HaveClosed;
            public MatrixFrame Anim;
            public bool HaveAnim;
            public bool Held;
            public long LogAt;
        }

        private static readonly Side[] _sides = { new Side(), new Side() };
        private static bool _broken;

        /// <summary>Before any bone is written this frame: the engine's own
        /// finger0, per hand - its position and scale are kept.</summary>
        internal static void BeforeWrites(Skeleton skeleton, VrHandSlot main, VrHandSlot off)
        {
            if (!Enabled || _broken || skeleton == null)
                return;

            try
            {
                Read(skeleton, main);
                Read(skeleton, off);
            }
            catch (Exception ex)
            {
                Break(ex);
            }
        }

        /// <summary>After the weapons are placed; rebuilds the entitial frames
        /// when it wrote.</summary>
        internal static void AfterWeapons(Skeleton skeleton, BvrInputState input, VrHandSlot main, VrHandSlot off)
        {
            if (!Enabled || _broken || skeleton == null)
                return;

            try
            {
                // A bow or crossbow is a main-hand item carried on the off hand (TEST 231).
                const TaleWorlds.Core.EquipmentIndex none = TaleWorlds.Core.EquipmentIndex.None;
                bool offCarries = main.Carrier == HandRole.Off && main.Equipment != none;
                bool mainHolds = main.Equipment != none && !offCarries;
                bool offHolds = off.Equipment != none || offCarries;
                bool twoHand = !offHolds && VrTwoHand.Active && main.Equipment != none;

                Grip mainGrip = mainHolds ? Grip.Held : Grip.Free;
                Grip offGrip = offHolds ? Grip.Held : twoHand ? Grip.TwoHand : Grip.Free;
                if (offCarries && VrCrossbow.Active)
                {
                    // TEST 373: either hand may have let go of the crossbow.
                    mainGrip = VrCrossbow.Holds(main.Controller) ? Grip.Held : Grip.Free;
                    offGrip = VrCrossbow.Holds(off.Controller) ? Grip.Held : Grip.Free;
                }

                if (VrBow.HoldsArrow && VrBow.StringController == main.Controller)
                    mainGrip = Grip.Hook;
                else if (VrBow.HoldsArrow && VrBow.StringController == off.Controller)
                    offGrip = Grip.Hook;

                // TEST 520: an empty hand on a ladder or the ballista's ring is a fist.
                if (mainGrip == Grip.Free && Fist(main.Controller))
                    mainGrip = Grip.Fist;
                if (offGrip == Grip.Free && Fist(off.Controller))
                    offGrip = Grip.Fist;

                Hold(main, mainGrip, HandRole.Main);
                Hold(off, offGrip, offCarries || twoHand ? HandRole.Main : HandRole.Off);
                mainHolds = mainGrip == Grip.Held;
                offHolds = offGrip == Grip.Held || offGrip == Grip.TwoHand;

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                float dt = _curlAt == 0 ? 0f : Math.Min(0.1f, (now - _curlAt) / (float)System.Diagnostics.Stopwatch.Frequency);
                _curlAt = now;
                uint touch = Touch();
                Curls(input, touch, main, dt);
                Curls(input, touch, off, dt);

                bool wrote;
                if (VrHandMesh.Live)
                {
                    // Our copies bend the fingers; the GPU must add nothing.
                    wrote = Neutral(skeleton, main);
                    wrote |= Neutral(skeleton, off);
                }
                else
                {
                    wrote = Write(skeleton, input, main, mainHolds, HandRole.Main);
                    wrote |= Write(skeleton, input, off, offHolds, offCarries ? HandRole.Main : HandRole.Off);
                }

                if (wrote)
                    skeleton.UpdateEntitialFramesFromLocalFrames();
            }
            catch (Exception ex)
            {
                Break(ex);
            }
        }

        private static bool Fist(VrHand hand) => VrLadder.Holds(hand) || VrBallista.Holds((int)hand);

        private static void Read(Skeleton skeleton, VrHandSlot slot)
        {
            Side s = SideOf(skeleton, slot);
            if (s == null)
                return;

            s.Anim = skeleton.GetBoneEntitialFrameWithIndex(s.Hand)
                .TransformToLocalNonOrthogonal(skeleton.GetBoneEntitialFrameWithIndex(s.Item));
            s.HaveAnim = true;
        }

        private static int SideIndex(VrHandSlot slot) => slot.Controller == VrHand.Left ? 0 : 1;

        private static void Hold(VrHandSlot slot, Grip grip, HandRole role)
        {
            int side = SideIndex(slot);
            bool was = Holding(side);
            Grips[side] = grip;
            HeldRole[side] = role;

            // TEST 487: a hand that takes hold starts closing onto the handle
            // from where its fingers are, not from wrapped.
            if (!was && Holding(side))
            {
                _close[side] = new Vec3(Curl[side, 0], Curl[side, 1], 0f);
                _closeVel[side] = Vec3.Zero;
            }
        }

        /// <summary>TEST 482: the touch sensors (0 from a native DLL without them).</summary>
        private static uint Touch()
        {
            if (_noTouch)
                return 0;

            try
            {
                return NativeMethods.bvr_get_finger_touch();
            }
            catch (EntryPointNotFoundException)
            {
                _noTouch = true;
                VrLog.Info("TEST 482: this native DLL has no touch sensors; the fingers follow the trigger and grip alone.");
                return 0;
            }
        }

        private static void Curls(BvrInputState input, uint touch, VrHandSlot slot, float dt)
        {
            int side = SideIndex(slot);
            BvrHandState hand = side == 0 ? input.Left : input.Right;
            // The wrapper swapped trigger and grip once (controls_swap_trigger_grip);
            // this undoes it - the fingers follow the PHYSICAL controls.
            float squeeze = Math.Max(0f, Math.Min(1f, Swapped ? hand.Trigger : hand.Grip));
            float trigger = Math.Max(0f, Math.Min(1f, Swapped ? hand.Grip : hand.Trigger));
            uint t = (touch >> (8 * side)) & 0xFFu;

            uint bound = t & (VrFingerRig.TouchTriggerBound | VrFingerRig.TouchThumbBound);
            if (bound != _bound[side])
            {
                _bound[side] = bound;
                VrLog.Info("TEST 482 " + slot.Controller + ": trigger touch "
                           + ((bound & VrFingerRig.TouchTriggerBound) != 0 ? "bound" : "NOT bound (the pull stands in)")
                           + ", thumb touch "
                           + ((bound & VrFingerRig.TouchThumbBound) != 0 ? "bound." : "NOT bound (the grip stands in)."));
            }

            VrFingerRig.Engaged(trigger, t, out bool index, out bool thumb);

            // TEST 501: a hand that holds something closes its index and thumb on
            // it too, touched or not - 482 lifted them off whenever the trigger or
            // the stick was let go, and they stood up off the grip.
            if (Holding(side) || Grips[side] == Grip.Fist)
                index = thumb = true;
            IndexEngaged[side] = index;
            ThumbEngaged[side] = thumb;

            Vec3 c;
            switch (Grips[side])
            {
                case Grip.Free:
                    c = VrFingerRig.FreeCurls(trigger, squeeze, t, IndexTouch, ThumbTouch);
                    break;
                case Grip.Hook:
                    c = new Vec3(Hook[0], Hook[1], Hook[2]);
                    break;
                case Grip.Fist:
                    c = new Vec3(1f, 1f, 1f);
                    break;
                default:
                    // TEST 494: until the hold's copies show, the grid closes toward
                    // the curls the wrap works out to - so the switch to them is not
                    // a pop from a generic 0.75 - with an index or thumb that is off
                    // the handle relaxed, as the hold copies have it.
                    c = Grips[side] != Grip.Curl && VrHandMesh.TryHoldCurls(side, out Vec3 w)
                        ? new Vec3(thumb ? w.x : 0f, index ? w.y : 0f, w.z)
                        : new Vec3(ThumbHold, HoldCurl, HoldCurl);
                    break;
            }

            VrWeight.Spring(ref _curl[side], ref _curlVel[side], c, Omega, Damp, dt);
            Curl[side, 0] = Clamp01(_curl[side].x);
            Curl[side, 1] = Clamp01(_curl[side].y);
            Curl[side, 2] = Curl[side, 3] = Curl[side, 4] = Clamp01(_curl[side].z);

            VrWeight.Spring(ref _close[side], ref _closeVel[side], new Vec3(thumb ? 1f : 0f, index ? 1f : 0f, 0f), Omega, Damp, dt);
            Close[side, 0] = Clamp01(_close[side].x);
            Close[side, 1] = Clamp01(_close[side].y);
        }

        private static float Clamp01(float v) => Math.Max(0f, Math.Min(1f, v));

        /// <summary>finger0 at its rest pose (position and scale the engine's).</summary>
        private static bool Neutral(Skeleton skeleton, VrHandSlot slot)
        {
            Side s = SideOf(skeleton, slot);
            if (s == null || !s.HaveAnim)
                return false;

            MatrixFrame local = s.Anim;
            local.rotation = s.Rest.ToMat3();
            local.rotation.ApplyScaleLocal(Scale(s.Anim.rotation));
            skeleton.SetBoneLocalFrame(s.Item, local);
            return true;
        }

        private static bool Write(Skeleton skeleton, BvrInputState input, VrHandSlot slot, bool holding, HandRole heldRole)
        {
            Side s = SideOf(skeleton, slot);
            if (s == null || !s.HaveAnim)
                return false;

            Quaternion q;
            if (holding)
            {
                if (!VrSkinnedWeapon.TryGripLocal(heldRole, out MatrixFrame grip))
                {
                    s.Held = false;
                    return false;
                }

                q = Rot(grip);
                s.Closed = q;
                s.HaveClosed = true;
                if (!s.Held)
                {
                    s.Held = true;
                    VrLog.Info("TEST 480 " + slot.Controller + ": finger0 holds the " + heldRole
                               + " weapon's grip, " + Degrees(s.Rest, q).ToString("F0", CultureInfo.InvariantCulture)
                               + " deg from rest - the walk and strafe animations no longer move this hand.");
                }
            }
            else
            {
                s.Held = false;
                BvrHandState hand = slot.Controller == VrHand.Left ? input.Left : input.Right;
                // The physical grip, whatever controls_swap_trigger_grip says.
                float grip = Math.Max(0f, Math.Min(1f, Swapped ? hand.Trigger : hand.Grip));

                Quaternion closed = s.HaveClosed ? s.Closed : Turn(s.Rest, CloseDeg);
                if (closed.Dotp4(s.Rest) < 0f)
                    closed = closed * -1f;
                q = Quaternion.Slerp(s.Rest, closed, grip);
                q.SafeNormalize();

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (grip > 0.05f && now >= s.LogAt)
                {
                    s.LogAt = now + 2 * System.Diagnostics.Stopwatch.Frequency;
                    VrLog.Info("TEST 480 " + slot.Controller + ": empty hand, grip "
                               + grip.ToString("F2", CultureInfo.InvariantCulture) + ", curl "
                               + Degrees(s.Rest, q).ToString("F0", CultureInfo.InvariantCulture) + " deg toward "
                               + (s.HaveClosed ? "the last held grip." : "finger_axis."));
                }
            }

            // The rotation only: the position is the engine's, the grip point.
            MatrixFrame local = s.Anim;
            local.rotation = q.ToMat3();
            local.rotation.ApplyScaleLocal(Scale(s.Anim.rotation));
            skeleton.SetBoneLocalFrame(s.Item, local);
            return true;
        }

        private static Side SideOf(Skeleton skeleton, VrHandSlot slot)
        {
            if (!slot.Valid || slot.Bone < 0)
                return null;

            Side s = _sides[slot.Controller == VrHand.Left ? 0 : 1];
            if (s.Skeleton == skeleton.Pointer && s.Hand == slot.Bone)
                return s.Item >= 0 ? s : null;

            s.Skeleton = skeleton.Pointer;
            s.Hand = slot.Bone;
            s.Item = -1;
            s.HaveAnim = s.Held = false;

            // The hand's only child on this rig is its finger0 (the item bone).
            int count = skeleton.GetBoneCount();
            for (sbyte b = 0; b < count; b++)
            {
                if (skeleton.GetParentBoneIndex(b) == slot.Bone)
                {
                    s.Item = b;
                    break;
                }
            }

            if (s.Item < 0)
                return null;

            s.Rest = Rot(skeleton.GetBoneEntitialRestFrame(slot.Bone, false)
                .TransformToLocalNonOrthogonal(skeleton.GetBoneEntitialRestFrame(s.Item, false)));
            VrLog.Info("TEST 480 " + slot.Controller + ": finger0 is '" + skeleton.GetBoneName(s.Item) + "' ["
                       + s.Item + "] on '" + skeleton.GetBoneName(slot.Bone) + "'; written after the weapons.");
            return s;
        }

        /// <summary>The rest rotation turned about finger_axis, in the bone's own
        /// rest axes.</summary>
        private static Quaternion Turn(Quaternion rest, float degrees)
        {
            Mat3 r = rest.ToMat3();
            Vec3 axis = r.TransformToParent(Axis).NormalizedCopy();
            Mat3 turned = r;
            turned.RotateAboutAnArbitraryVector(axis, degrees * (float)(Math.PI / 180.0));
            return turned.ToQuaternion();
        }

        private static Quaternion Rot(MatrixFrame f)
        {
            Mat3 r = f.rotation;
            r.Orthonormalize();
            return r.ToQuaternion();
        }

        private static Vec3 Scale(Mat3 m) => new Vec3(m.s.Length, m.f.Length, m.u.Length);

        private static float Degrees(Quaternion a, Quaternion b)
        {
            float dot = Math.Min(1f, Math.Abs(a.Dotp4(b)));
            return (float)(2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }

        private static Vec3 ParseAxis(string text)
        {
            string t = (text ?? "y").Trim().ToLowerInvariant();
            float sign = t.StartsWith("-") ? -1f : 1f;
            char c = t.TrimStart('-', '+').Length > 0 ? t.TrimStart('-', '+')[0] : 'y';
            return c == 'x' ? new Vec3(sign, 0f, 0f) : c == 'z' ? new Vec3(0f, 0f, sign) : new Vec3(0f, sign, 0f);
        }

        internal static void Reset()
        {
            foreach (Side s in _sides)
            {
                s.Skeleton = UIntPtr.Zero;
                s.Hand = s.Item = -1;
                s.HaveClosed = false;
            }
        }

        private static void Break(Exception ex)
        {
            _broken = true;
            VrLog.Error("TEST 480: the finger bone write failed; the hands keep the engine's pose. Nothing else is affected.", ex);
        }
    }
}
