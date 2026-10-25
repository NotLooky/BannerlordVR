using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;

namespace BannerlordVR.VR
{
    /// <summary>
    /// Weapons hanging on the body (Phase 2 of the B&amp;S plan).
    ///
    /// TEST 328b: the player's own sheathed weapons, VISIBLE, hanging from the
    /// body we draw. TEST 328 measured the engine's own placement: a sheathed
    /// weapon is its slot entity at bone x MBItem.GetHolsterFrameByIndex(h),
    /// exactly (0.000 m, 0 deg) - 'pelvis' for the hips, 'spine2' for the back -
    /// and in first person the engine masks it out (0, or 32 = shadow only), so
    /// the player never sees their own holsters.
    ///
    /// So each weapon slot gets an entity of our own carrying copies of the
    /// slot's meshes (mask 33). While the engine has the item sheathed, Learn
    /// finds which bone and holster it hangs from (the TEST 328 fit, kept only
    /// under 2 cm). Tick then hangs the copies from OUR posed bone after the
    /// last rebuild - holster frame times the item's HolsterPositionShift - so
    /// they ride the drawn body, not the animation's. Until a slot is learned
    /// its copies sit on the engine's entity frame.
    ///
    /// TEST 328c: never the engine's METAMESH frames. A weapon that has been in
    /// a hand keeps its hand-regime mesh offset after the engine sheathes it
    /// (TEST 328 at 03:11:09: a sheathed sword's mesh at (0.125, -0.55, 0.12) -
    /// harmless to the engine, which masks it out anyway). 328b learned that
    /// offset, and every weapon that had been drawn once hung in mid-air or at
    /// a wrong place on the body. Only the ENTITY frame is placed at the
    /// holster (the fit's 0.000 m), so only it is trusted.
    ///
    /// Both hips (the user's choice): a one-hander whose vanilla hip is already
    /// taken by an earlier one-hander goes to the other hip, its vanilla frame
    /// mirrored across the bone's lateral axis (holster_mirror_axis).
    ///
    /// TEST 329: the grab zones are the copies (Near), and a draw glides the
    /// weapon from where it hangs into the hand; a sheathe glides it back.
    ///
    /// TEST 328 (holster_view_probe): the dump that measured all of the above.
    /// </summary>
    internal static class VrHolsterView
    {
        internal static readonly bool Enabled = VrConfig.Bool("holster_view", true);
        internal static readonly float GrabM = VrConfig.Float("holster_grab_m", 0.15f);

        /// <summary>TEST 335: a hanging copy this close to the eyes is hidden (the
        /// engine hides all of them in first person for the same reason). Shown
        /// again 5 cm further out, so it does not flicker on the edge.</summary>
        private static readonly float ClearM = VrConfig.Float("holster_clear_m", 0.25f);

        /// <summary>TEST 504: the wearer's body, where a back weapon is hidden (shown
        /// again 2 cm out): from the neck (TEST 506; 504: half way up) down to the knees
        /// (holster_leg_m below the pelvis), holster_body_half_m either side,
        /// everything in front of the back's surface - holster_back_m behind the
        /// body's centre line, in the hips' facing. 0 turns it off.</summary>
        private static readonly float BackM = VrConfig.Float("holster_back_m", 0.10f);
        private static readonly float BodyHalfM = VrConfig.Float("holster_body_half_m", 0.20f);
        private static readonly float LegM = VrConfig.Float("holster_leg_m", 0.85f);

        private static UIntPtr _torsoSkeleton;
        private static sbyte _pelvis = -1, _neck = -1, _lShoulder = -1, _rShoulder = -1;
        private static Vec3 _torsoA, _torsoB, _bodyFwd, _bodySide, _bodyUp, _chestFwd, _chestSide;
        private static bool _haveTorso;
        private static long _torsoLogAt;

        /// <summary>TEST 336: the engine's own meshes come off a holstered slot
        /// while our copy hangs (the TEST 229 rule: remove, never mask).</summary>
        private static readonly bool TakeOffOriginals = VrConfig.Bool("holster_take_off", true);

        /// <summary>TEST 337: hip weapons moved from the vanilla spot, in the
        /// body's axes - inward (toward the centre), forward, up, metres.
        /// sword_left_hip_3 sits 0.22 m out and 0.07 m BEHIND the pelvis.</summary>
        private static readonly Vec3 HipOffset =
            VrBow.ParseZone(VrConfig.String("holster_hip_offset", "0.04,0.12,0.00")) ?? new Vec3(0.04f, 0.12f, 0f);
        private static readonly int MirrorAxis = Math.Max(0,
            "xyz".IndexOf((VrConfig.String("holster_mirror_axis", "z").Trim().ToLowerInvariant() + "z")[0]));
        /// <summary>TEST 350: the sheath look hangs at the holster frame itself
        /// (TEST 335: HolsterPositionShift places the weapon INSIDE a holster
        /// look). 1 also shifts the look, if the blade sits off its sheath.</summary>
        private static readonly bool LookShift = VrConfig.Bool("holster_look_shift", false);

        private const float FitM = 0.02f;
        private const int Slots = 4;
        private const int MaxFitTries = 120;

        private sealed class Hung
        {
            public string ItemId;
            public int Amount;
            public bool OneHanded;
            public string Side;                 // "L", "R" or null, from the item's hip holster name
            public bool SingleLook;             // the weapon itself hangs (no holster look)
            public UIntPtr Source;              // the engine's slot entity the originals belong to
            public readonly List<MetaMesh> Originals = new List<MetaMesh>();
            public int ReAdds;
            public bool Hips;                   // TEST 467: hung from the pelvis, never hidden
            public Vec3 BoxMin, BoxMax;         // the hung look's box, in its own space
            public bool NearEye;
            public bool InBody;                 // TEST 503: its length runs through the wearer's torso
            public readonly List<MetaMesh> Copies = new List<MetaMesh>();
            public readonly List<MatrixFrame> Local = new List<MatrixFrame>();
            public sbyte Bone = -1;
            public string Holster;
            public int FitTries;
            public bool Mirror;
            public bool Wielded;
            public bool HaveHome;
            public MatrixFrame Home;            // the first copy's visual frame, world
            public MatrixFrame LastHand;        // the weapon's visual in the hand, last wielded tick
            public bool HaveLastHand;
            public long GlideStart;
            public float GlideDur;
            public MatrixFrame GlideFrom;       // the weapon's VISUAL frame, world, at the start

            // TEST 350: a crafted weapon's sheath, ours alone (SheathPatch keeps
            // the engine's off): with the blade in it while sheathed, empty on the
            // hip while drawn. Null for an item without one.
            public MetaMesh LookFull;
            public MetaMesh LookEmpty;
            public bool LookChecked;
            public bool WeaponShown = true;
            public bool FullShown = true;
            public bool EmptyShown = true;
        }

        private static readonly Hung[] _hung = new Hung[Slots];
        private static readonly GameEntity[] _entity = new GameEntity[Slots];

        /// <summary>TEST 372: slots whose weapon is flying home (VrThrow) - the
        /// holster shows empty, as if it were still in the hand.</summary>
        private static readonly bool[] _away = new bool[Slots];
        private static readonly Dictionary<string, MatrixFrame> _holsters = new Dictionary<string, MatrixFrame>();
        private static Mission _viewMission;
        private static bool _broken;

        /// <summary>From the start of VrWeaponHands' pass, before any bone is
        /// written: the probe, then learning where the engine hung each item.</summary>
        internal static void Learn(Agent agent, MBAgentVisuals visuals, Skeleton skeleton)
        {
            ProbeTick(agent, visuals, skeleton);

            if (!Enabled || _broken || agent == null || visuals == null || skeleton == null)
                return;

            try
            {
                NewMission();

                EquipmentIndex main = agent.GetPrimaryWieldedItemIndex();
                EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
                MatrixFrame agentFrame = visuals.GetFrame();
                Scene scene = visuals.GetEntity()?.Scene;

                // Only slots 0-3 that hold an item: the accessor is an unchecked
                // native passthrough (bl2vr-native-accessors-unbounded).
                for (int k = 0; k < Slots; k++)
                {
                    EquipmentIndex idx = (EquipmentIndex)k;
                    MissionWeapon weapon = agent.Equipment[idx];
                    ItemObject item = weapon.Item;
                    if (item == null)
                    {
                        Forget(k);
                        continue;
                    }

                    Hung h = _hung[k];
                    if (h != null && (h.ItemId != item.StringId || h.Amount != weapon.Amount))
                    {
                        Forget(k, agent);
                        h = null;
                    }

                    bool wielded = idx == main || idx == off;
                    if (h != null)
                        h.Wielded = wielded;

                    // TEST 336: drawn - the engine's meshes go back first, so this
                    // tick's VrSkinnedWeapon.Attach has them to copy (it runs after
                    // this, and takes them off again for the hand).
                    if (wielded)
                    {
                        if (h != null)
                            PutBack(h, agent, idx);
                        continue;
                    }

                    // Sheathed: the engine's slot entity hangs at the holster now.
                    WeakGameEntity e = agent.GetWeaponEntityFromEquipmentSlot(idx);
                    if (!e.IsValid)
                        continue;

                    List<MetaMesh> metas = Metas(e);
                    if (h == null)
                    {
                        if (metas.Count == 0 || scene == null || EngineHangsLook(item, metas))
                            continue;
                        h = Build(k, item, weapon.Amount, metas, scene);
                        if (h == null)
                            continue;
                        h.Source = e.Pointer;
                    }

                    // 334 left one copy of a multi-look slot, so a count match
                    // with the engine's meshes kept those from ever being fitted.
                    if (h.Copies.Count == 0)
                        continue;

                    if (h.Bone < 0 && h.FitTries < MaxFitTries)
                        Fit(h, item, skeleton, agentFrame, e.GetGlobalFrame(), k);

                    if (TakeOffOriginals && e.Pointer == h.Source && metas.Count > 0)
                        TakeOff(h, e, metas, k);                }
            }
            catch (Exception ex)
            {
                Break("learning where the weapons hang", ex);
            }
        }

        /// <summary>After VrWeaponHands, so the bones are the ones we drew: hangs
        /// every sheathed copy from them, and glides a fresh sheathe home.</summary>
        internal static void Tick()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                Agent agent = Mission.Current?.MainAgent;
                MBAgentVisuals visuals = agent?.AgentVisuals;
                Skeleton skeleton = visuals?.GetSkeleton();
                if (agent == null || !agent.IsActive() || skeleton == null || VrBodyHide.Hiding
                    || !ReferenceEquals(_viewMission, Mission.Current))
                {
                    HideAll();
                    return;
                }

                Assign();

                MatrixFrame agentFrame = visuals.GetFrame();
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                Torso(skeleton, agentFrame);

                for (int k = 0; k < Slots; k++)
                {
                    Hung h = _hung[k];
                    GameEntity entity = _entity[k];
                    if (h == null || entity == null)
                        continue;

                    if (h.Bone < 0)
                    {
                        // Not learned yet: where the engine put it, unmasked.
                        h.HaveHome = false;
                        Show(k, !h.Wielded && Place(h, entity, agent, k));
                        continue;
                    }

                    MatrixFrame at = agentFrame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(h.Bone));

                    // TEST 337: a hip weapon moved inward/forward/up in the body's
                    // axes, mirrored with it (inward is +right on the left hip).
                    if (h.Side != null)
                    {
                        bool left = (h.Side == "L") != h.Mirror;
                        Vec3 shift = new Vec3(left ? HipOffset.x : -HipOffset.x, HipOffset.y, HipOffset.z);
                        at.origin += agentFrame.rotation.TransformToParent(shift);
                    }

                    MatrixFrame first = h.Mirror ? Mirror(h.Local[0], MirrorAxis) : h.Local[0];
                    h.Home = at.TransformToParent(first);
                    h.HaveHome = true;

                    // TEST 350: the sheath hangs at the holster frame, moved and
                    // mirrored with the weapon.
                    MatrixFrame lookLocal = LookShift ? h.Local[0] : Holster(h.Holster);
                    if (h.Mirror)
                        lookLocal = Mirror(lookLocal, MirrorAxis);
                    SetFrame(h.LookFull, lookLocal);
                    SetFrame(h.LookEmpty, lookLocal);
                    CheckLook(h, k);

                    if (h.Wielded || _away[k])
                    {
                        // Where the hand has it, for the glide home. The empty
                        // sheath stays on the hip. Away, the flight is the glide.
                        h.HaveLastHand = h.Wielded && HandVisual(agent, (EquipmentIndex)k, out h.LastHand);
                        h.GlideStart = 0;
                        if (h.LookEmpty == null)
                        {
                            Show(k, false);
                            continue;
                        }

                        Looks(h, full: false, empty: true, bare: false);
                        entity.SetGlobalFrame(in at, isTeleportation: true);
                        Show(k, !InFace(h, at));
                        continue;
                    }

                    for (int m = 0; m < h.Copies.Count; m++)
                    {
                        MatrixFrame local = h.Mirror ? Mirror(h.Local[m], MirrorAxis) : h.Local[m];
                        if (!Same(h.Copies[m].Frame, local))
                            h.Copies[m].Frame = local;
                    }

                    // TEST 329: just sheathed - the WEAPON glides from the hand into
                    // its holster (the sheath stays where it hangs).
                    if (h.HaveLastHand)
                    {
                        h.HaveLastHand = false;
                        h.GlideFrom = h.LastHand;
                        h.GlideStart = now;
                        float d = (h.LastHand.origin - h.Home.origin).Length;
                        h.GlideDur = Math.Max(0.12f, Math.Min(0.30f, d / 2.5f));
                    }

                    bool gliding = false;
                    if (h.GlideStart != 0 && h.Copies.Count > 0)
                    {
                        float t = (float)((now - h.GlideStart) / (double)System.Diagnostics.Stopwatch.Frequency) / h.GlideDur;
                        if (t >= 1f)
                        {
                            h.GlideStart = 0;
                        }
                        else
                        {
                            gliding = true;
                            h.Copies[0].Frame = at.TransformToLocal(VrSkinnedWeapon.GlideAt(h.GlideFrom, h.Home, t));
                        }
                    }

                    // The full sheath IS the sheathed weapon; the bare weapon shows
                    // only while it glides home into the empty sheath.
                    bool full = h.LookFull != null && !gliding;
                    Looks(h, full, empty: !full, bare: !full);

                    entity.SetGlobalFrame(in at, isTeleportation: true);
                    Show(k, !InFace(h, at));
                }
            }
            catch (Exception ex)
            {
                Break("hanging the weapons", ex);
            }
        }

        /// <summary>TEST 329: the nearest holster (a hanging copy, or a held
        /// weapon's empty holster) to a hand point, among the slots `allowed`
        /// lets this hand take or put back.</summary>
        internal static bool Near(Vec3 world, Func<EquipmentIndex, bool> allowed, out EquipmentIndex slot)
        {
            slot = EquipmentIndex.None;
            if (!Enabled || _broken)
                return false;

            float best = GrabM;
            for (int k = 0; k < Slots; k++)
            {
                Hung h = _hung[k];
                if (h == null || !h.HaveHome)
                    continue;

                float d = (h.Home.origin - world).Length;
                if (d < best && allowed((EquipmentIndex)k))
                {
                    best = d;
                    slot = (EquipmentIndex)k;
                }
            }

            return slot != EquipmentIndex.None;
        }

        /// <summary>TEST 372: this slot's weapon is flying home (or has arrived).</summary>
        internal static void Away(EquipmentIndex slot, bool away)
        {
            int k = (int)slot;
            if (k >= 0 && k < Slots)
                _away[k] = away;
        }

        /// <summary>Where this slot's weapon is seen hanging now (the first
        /// copy), for a draw's glide.</summary>
        internal static bool TryVisual(EquipmentIndex slot, out MatrixFrame visual)
        {
            visual = MatrixFrame.Identity;
            int k = (int)slot;
            if (k < 0 || k >= Slots || _hung[k] == null || _entity[k] == null || _hung[k].Copies.Count == 0)
                return false;

            // TEST 372: Home is placed every tick, even while the slot's entity
            // is hidden (drawn, or flying home), so a homing weapon follows the body.
            visual = _hung[k].HaveHome
                ? _hung[k].Home
                : _entity[k].GetGlobalFrame().TransformToParent(_hung[k].Copies[0].Frame);
            return true;
        }

        /// <summary>A frame mirrored across one axis of its parent (0 = x, 1 = y,
        /// 2 = z): S R S with S the reflection, so it stays a proper rotation.
        /// The axis images are R's columns s, f, u: each is reflected, and the
        /// one along the mirror axis is negated as well. Pure; the math tests
        /// call it.</summary>
        internal static MatrixFrame Mirror(MatrixFrame f, int axis)
        {
            MatrixFrame m = f;
            m.origin = Flip(f.origin, axis);
            m.rotation.s = Flip(f.rotation.s, axis) * (axis == 0 ? -1f : 1f);
            m.rotation.f = Flip(f.rotation.f, axis) * (axis == 1 ? -1f : 1f);
            m.rotation.u = Flip(f.rotation.u, axis) * (axis == 2 ? -1f : 1f);
            return m;
        }

        private static Vec3 Flip(Vec3 v, int axis)
        {
            if (axis == 0)
                v.x = -v.x;
            else if (axis == 1)
                v.y = -v.y;
            else
                v.z = -v.z;
            return v;
        }

        /// <summary>Both hips: walk the slots in order; a one-hander whose
        /// vanilla hip is taken by an earlier item goes to the other hip if that
        /// one is free. Sides come from the holster NAMES (left_hip/right_hip).</summary>
        private static void Assign()
        {
            bool left = false, right = false;
            for (int k = 0; k < Slots; k++)
            {
                Hung h = _hung[k];
                if (h == null)
                    continue;

                h.Mirror = false;
                if (h.Side == null)
                    continue;

                bool isLeft = h.Side == "L";
                bool taken = isLeft ? left : right;
                bool other = isLeft ? right : left;

                if (h.OneHanded && taken && !other)
                {
                    h.Mirror = true;
                    if (isLeft)
                        right = true;
                    else
                        left = true;
                }
                else if (isLeft)
                {
                    left = true;
                }
                else
                {
                    right = true;
                }
            }
        }

        private static Hung Build(int k, ItemObject item, int amount, List<MetaMesh> metas, Scene scene)
        {
            GameEntity entity = _entity[k];
            if (entity == null)
            {
                entity = GameEntity.CreateEmptyDynamic(scene, false);
                _entity[k] = entity;
            }

            var h = new Hung
            {
                ItemId = item.StringId,
                Amount = amount,
                OneHanded = item.ItemType == ItemObject.ItemTypeEnum.OneHandedWeapon,
                Side = SideOf(item),
                SingleLook = metas.Count == 1,
            };

            // TEST 334: a slot can carry several LOOKS of one item - the single
            // weapon (item 'mesh') and its holster with the weapon in it (a
            // quiver of arrows: mesh="arrow_bl_b", holster_mesh=
            // "arrow_bl_b_quiver"; a bundle of javelins) - and the engine shows
            // one per state by mask. Hung up, that is the holster look, which
            // contains the weapon, so the LARGEST. Copying them all drew a lone
            // arrow sticking out of the quiver and a second javelin.
            // TEST 412: by NAME where the item names its holster look (HolsterLook).
            string why = null;
            MetaMesh keep = metas.Count > 1 ? HolsterLook(item, metas, out why) : null;
            if (keep != null)
                VrLog.Info("TEST 334 [" + (EquipmentIndex)k + "] '" + item.StringId + "': " + metas.Count
                           + " looks (" + Sizes(metas) + "); hanging '" + keep.GetName() + "' (TEST 412: " + why + ").");

            foreach (MetaMesh meta in metas)
            {
                if (keep != null && meta != keep)
                    continue;

                MetaMesh copy = meta.CreateCopy();
                if (copy == null)
                    continue;

                int n = copy.MeshCount;
                for (int i = 0; i < n && i < 32; i++)
                    copy.GetMeshAtIndex(i)?.SetVisibilityMask((VisibilityMaskFlags)33);

                copy.Frame = MatrixFrame.Identity;
                VrSkinnedWeapon.Opaque(copy);
                if (h.Copies.Count == 0)
                    Box(copy, out h.BoxMin, out h.BoxMax);
                entity.AddMultiMesh(copy, true);
                h.Copies.Add(copy);
                h.Local.Add(MatrixFrame.Identity);
            }

            // TEST 350: the sheath, from the same View helpers OnGetWeaponData
            // uses - non-batched, for a dynamic entity.
            if (OwnsSheath(item))
            {
                h.LookFull = AddLook(entity, item.GetHolsterWithWeaponMeshCopy(false));
                h.LookEmpty = AddLook(entity, item.GetHolsterMeshCopy());
            }

            entity.SetVisibilityExcludeParents(false);
            _hung[k] = h;
            VrLog.Info("TEST 328b [" + (EquipmentIndex)k + "] '" + h.ItemId + "': " + h.Copies.Count
                       + " mesh(es) copied to hang visibly (the slot carries " + Sizes(metas) + ")"
                       + (h.Side != null ? ", " + h.Side + " hip" : "")
                       + (h.LookFull != null || h.LookEmpty != null
                           ? "; TEST 350 sheath '" + Name(h.LookFull) + "', empty '" + Name(h.LookEmpty) + "'"
                           : "") + ".");
            return h;
        }

        /// <summary>TEST 350: an item whose sheath is ours - not thrown, and crafted
        /// or (TEST 461) naming a holster look in its XML (SheathPatch strips the
        /// engine's for exactly these). A bow's is the bow in its holder
        /// (composite_steppe_bow: 'shortbow_d_holster_weapon'): left to the engine,
        /// it hung beside our copy of the bare bow - two bows on the hip.</summary>
        internal static bool OwnsSheath(ItemObject item)
        {
            return item != null && item.PrimaryWeapon != null && !item.PrimaryWeapon.IsConsumable
                   && (item.WeaponDesign != null || !string.IsNullOrEmpty(item.HolsterMeshName)
                       || !string.IsNullOrEmpty(item.HolsterWithWeaponMeshName));
        }

        /// <summary>TEST 466: a quiver's look is not on its slot entity - only the
        /// bare arrow and the FLYING one are (bodkin_arrows_a: 'arrow_bl_b',
        /// 'arrow_bl_flying' 3.14 m - TEST 412's "largest" fallback hung that,
        /// the long stick out of the quiver). The engine draws the quiver itself
        /// (WeaponData.HolsterMeshWithWeapon, left on for anything thrown or
        /// shot), so nothing of ours hangs for it.</summary>
        private static bool EngineHangsLook(ItemObject item, List<MetaMesh> metas)
        {
            if (item.PrimaryWeapon == null || !item.PrimaryWeapon.IsConsumable
                || (string.IsNullOrEmpty(item.HolsterWithWeaponMeshName) && string.IsNullOrEmpty(item.HolsterMeshName)))
                return false;

            foreach (MetaMesh meta in metas)
            {
                string name = meta.GetName();
                if (name != item.MultiMeshName && name != item.FlyingMeshName)
                    return false;
            }

            if (_engineLooks.Add(item.StringId))
                VrLog.Info("TEST 466: '" + item.StringId + "' has only its bare and flying looks on the slot ("
                           + Sizes(metas) + "); the engine hangs its holster look '"
                           + item.HolsterWithWeaponMeshName + "', so nothing of ours does.");
            return true;
        }

        private static readonly HashSet<string> _engineLooks = new HashSet<string>();

        private static MetaMesh AddLook(GameEntity entity, MetaMesh look)
        {
            if (look == null)
                return null;

            int n = look.MeshCount;
            for (int i = 0; i < n && i < 32; i++)
                look.GetMeshAtIndex(i)?.SetVisibilityMask((VisibilityMaskFlags)33);
            look.Frame = MatrixFrame.Identity;
            entity.AddMultiMesh(look, true);
            return look;
        }

        private static string Name(MetaMesh meta) => meta == null ? "-" : meta.GetName();

        private static void SetFrame(MetaMesh meta, MatrixFrame frame)
        {
            if (meta != null && !Same(meta.Frame, frame))
                meta.Frame = frame;
        }

        /// <summary>Which of the slot's looks are drawn: the sheath with the
        /// blade in it, the empty sheath, the bare weapon.</summary>
        private static void Looks(Hung h, bool full, bool empty, bool bare)
        {
            if (h.LookFull != null)
                SetShown(h.LookFull, full, ref h.FullShown);
            if (h.LookEmpty != null)
                SetShown(h.LookEmpty, empty, ref h.EmptyShown);
            foreach (MetaMesh copy in h.Copies)
                SetShown(copy, bare, ref h.WeaponShown);
        }

        /// <summary>TEST 350, once per item: how far the bare blade's centre at
        /// home sits from the full sheath's. A few cm = the blade glides into the
        /// sheath; much more = holster_look_shift is the other way.</summary>
        private static void CheckLook(Hung h, int k)
        {
            if (h.LookChecked || h.LookFull == null || h.Copies.Count == 0)
                return;
            h.LookChecked = true;

            Box(h.LookFull, out Vec3 lo, out Vec3 hi);
            MatrixFrame sheathAt = LookShift ? h.Local[0] : Holster(h.Holster);
            Vec3 sheath = sheathAt.TransformToParent((lo + hi) * 0.5f);
            Vec3 blade = h.Local[0].TransformToParent((h.BoxMin + h.BoxMax) * 0.5f);
            VrLog.Info("TEST 350 [" + (EquipmentIndex)k + "] '" + h.ItemId + "': our sheath '" + Name(h.LookFull)
                       + "' (" + Size(h.LookFull).ToString("F2", CultureInfo.InvariantCulture) + " m) hangs at '"
                       + h.Holster + "'; the bare blade's centre sits "
                       + (sheath - blade).Length.ToString("F3", CultureInfo.InvariantCulture)
                       + " m from the sheath's (a few cm = it slides in; holster_look_shift "
                       + (LookShift ? "1" : "0") + ").");
        }

        /// <summary>Which of the item's holsters, on which bone, puts it where
        /// the engine has it (TEST 328: 0.000 m when right).</summary>
        private static void Fit(Hung h, ItemObject item, Skeleton skeleton, MatrixFrame agentFrame,
                                MatrixFrame entityGlobal, int k)
        {
            h.FitTries++;
            string[] holsters = item.ItemHolsters;
            if (holsters == null)
                return;

            float best = float.MaxValue;
            sbyte bestBone = -1;
            string bestName = null;
            int bones = skeleton.GetBoneCount();

            foreach (string name in holsters)
            {
                if (string.IsNullOrEmpty(name))
                    continue;

                MatrixFrame holster = Holster(name);
                for (sbyte b = 0; b < bones; b++)
                {
                    Vec3 at = agentFrame.TransformToParent(
                        skeleton.GetBoneEntitialFrameWithIndex(b).TransformToParent(holster)).origin;
                    float d = (at - entityGlobal.origin).Length;
                    if (d < best)
                    {
                        best = d;
                        bestBone = b;
                        bestName = name;
                    }
                }
            }

            if (best <= FitM)
            {
                h.Bone = bestBone;
                h.Holster = bestName;
                h.Hips = skeleton.GetBoneName(bestBone) == "pelvis";

                // The item hangs at the holster. A weapon that IS its own look
                // (a shield, a sword) is shifted by its HolsterPositionShift in
                // the holster's space (the shield's fresh metamesh had it,
                // TEST 328). A holster look (quiver, javelin bundle) is not: the
                // shift places the WEAPON inside it (bodkin arrows: 0.15 m), and
                // applied to the quiver it rode 15 cm up the back (TEST 335).
                MatrixFrame local = Holster(bestName);
                if (h.SingleLook)
                    local.origin = local.TransformToParent(item.HolsterPositionShift);
                for (int m = 0; m < h.Local.Count; m++)
                    h.Local[m] = local;

                VrLog.Info("TEST 328b [" + (EquipmentIndex)k + "] '" + h.ItemId + "' hangs on '" + bestName
                           + "' from bone '" + skeleton.GetBoneName(bestBone) + "' ("
                           + best.ToString("F3", CultureInfo.InvariantCulture) + " m).");
            }
            else if (h.FitTries == MaxFitTries)
            {
                VrLog.Info("TEST 328b [" + (EquipmentIndex)k + "] '" + h.ItemId + "': no holster x bone came within "
                           + FitM.ToString("F2", CultureInfo.InvariantCulture) + " m (best "
                           + best.ToString("F3", CultureInfo.InvariantCulture) + "); it stays where the engine puts it.");
            }
        }

        private static MatrixFrame Holster(string name)
        {
            if (!_holsters.TryGetValue(name, out MatrixFrame frame))
            {
                int index = MBItem.GetItemHolsterIndex(name);
                frame = index >= 0 ? MBItem.GetHolsterFrameByIndex(index) : MatrixFrame.Identity;
                _holsters[name] = frame;
            }
            return frame;
        }

        private static string SideOf(ItemObject item)
        {
            if (item.ItemHolsters == null)
                return null;

            foreach (string name in item.ItemHolsters)
            {
                if (name == null)
                    continue;
                if (name.Contains("left_hip"))
                    return "L";
                if (name.Contains("right_hip"))
                    return "R";
            }
            return null;
        }

        /// <summary>Not learned yet: the copies on the engine's own entity frame
        /// (never its mesh frames - TEST 328c), shifted like a learned one.</summary>
        private static bool Place(Hung h, GameEntity entity, Agent agent, int k)
        {
            WeakGameEntity e = agent.GetWeaponEntityFromEquipmentSlot((EquipmentIndex)k);
            ItemObject item = agent.Equipment[(EquipmentIndex)k].Item;
            if (!e.IsValid || item == null)
                return false;

            MatrixFrame shift = MatrixFrame.Identity;
            if (h.SingleLook)
                shift.origin = item.HolsterPositionShift;
            foreach (MetaMesh copy in h.Copies)
                SetFrame(copy, shift);
            SetFrame(h.LookFull, MatrixFrame.Identity);
            SetFrame(h.LookEmpty, MatrixFrame.Identity);
            Looks(h, full: h.LookFull != null, empty: false, bare: h.LookFull == null);

            MatrixFrame at = e.GetGlobalFrame();
            entity.SetGlobalFrame(in at, isTeleportation: true);
            return true;
        }

        /// <summary>The held weapon's visual (VrSkinnedWeapon's entity times its
        /// mesh frame), for whichever hand holds this slot.</summary>
        private static bool HandVisual(Agent agent, EquipmentIndex slot, out MatrixFrame visual)
        {
            visual = MatrixFrame.Identity;
            HandRole role = slot == agent.GetOffhandWieldedItemIndex() ? HandRole.Off : HandRole.Main;
            if (!VrSkinnedWeapon.TryGetVisual(role, out MatrixFrame world, out List<MetaMesh> meshes, out int _, out string _)
                || meshes == null || meshes.Count == 0)
                return false;

            visual = world.TransformToParent(meshes[0].Frame);
            return true;
        }

        /// <summary>TEST 335: whether the hung look comes within holster_clear_m
        /// of the eyes - a javelin bundle on the back reached into the view on
        /// a horse, where the camera sits 0.10 m back (TEST 307).</summary>
        private static bool InFace(Hung h, MatrixFrame entityWorld)
        {
            // TEST 467: only the back's weapons reach the eyes on their own (TEST
            // 335's javelins). A hip bow's upper limb came within holster_clear_m
            // of a head bent down to look at it, and the bow vanished.
            if (h.Hips || !VrCameraDriver.HasPose || h.Copies.Count == 0 || h.BoxMax.x < h.BoxMin.x)
                return false;

            MatrixFrame look = entityWorld.TransformToParent(h.Copies[0].Frame);
            Vec3 eye = look.TransformToLocal(VrCameraDriver.HeadFrame.origin);
            float d = VrGrab.BoxDistance(eye, h.BoxMin, h.BoxMax);
            h.NearEye = d < (h.NearEye ? ClearM + 0.05f : ClearM);
            return h.NearEye | InTorso(h, look);
        }

        /// <summary>
        /// TEST 503: whether a back weapon's length runs through the wearer's body.
        /// The holster hangs from a chest bone (spine2) that the head-driven body
        /// turns and bends under the head - War Sails had the chest at -45 deg yaw
        /// and 40 deg of hip hinge - and a 1.78 m longbow hung from it swung through
        /// the hips and out in front of the player ("holstered inside me").
        /// TEST 504: 503 hid it only within 8 cm of the pelvis-to-neck LINE - far
        /// inside a body 25 cm deep, and blind to the legs: the bow was still seen
        /// through the player. Now the look's long axis (its box's longest side,
        /// through the box's centre), sampled, against the body as a volume (BackM,
        /// BodyHalfM, LegM) - the hips and legs, where the lower limb swings; the
        /// chest's back, turned with the head, is never in view.
        /// </summary>
        private static bool InTorso(Hung h, MatrixFrame look)
        {
            if (!_haveTorso || BackM <= 0f)
                return false;

            Vec3 size = h.BoxMax - h.BoxMin;
            Vec3 c = (h.BoxMin + h.BoxMax) * 0.5f;
            Vec3 half = size.x >= size.y && size.x >= size.z ? new Vec3(size.x * 0.5f, 0f, 0f)
                        : size.y >= size.z ? new Vec3(0f, size.y * 0.5f, 0f) : new Vec3(0f, 0f, size.z * 0.5f);
            Vec3 a = look.TransformToParent(c - half), b = look.TransformToParent(c + half);

            // How far in front of the back's surface its deepest point is, and where.
            Vec3 spine = _torsoB - _torsoA;
            float rise = Vec3.DotProduct(spine, _bodyUp);
            float worst = float.MinValue, worstUp = 0f, worstSide = 0f;
            for (int i = 0; i <= 16; i++)
            {
                Vec3 p = a + (b - a) * (i / 16f);
                Vec3 d = p - _torsoA;
                float up = Vec3.DotProduct(d, _bodyUp);
                if (up > rise || up < -LegM)
                    continue;

                // The centre line: up the spine above the pelvis, straight down below it.
                // TEST 506: up to the neck, facing the hips' way at the pelvis and the
                // chest's at the neck - 504 stopped half way up (the chest turns with
                // the head), and the bow through the shoulders stayed in view.
                Vec3 line = up >= 0f && rise > 1e-3f ? _torsoA + spine * (up / rise) : _torsoA + _bodyUp * up;
                float t = up > 0f && rise > 1e-3f ? up / rise : 0f;
                Vec3 fwd = (_bodyFwd + (_chestFwd - _bodyFwd) * t).NormalizedCopy();
                Vec3 across = (_bodySide + (_chestSide - _bodySide) * t).NormalizedCopy();
                Vec3 off = p - line;
                float side = Vec3.DotProduct(off, across);
                if (Math.Abs(side) > BodyHalfM)
                    continue;

                float front = Vec3.DotProduct(off, fwd) + BackM;
                if (front > worst)
                {
                    worst = front;
                    worstUp = up;
                    worstSide = side;
                }
            }

            bool was = h.InBody;
            h.InBody = worst > (was ? -0.02f : 0f);

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (h.InBody != was || (worst > -0.1f && now >= _torsoLogAt))
            {
                _torsoLogAt = now + 2 * System.Diagnostics.Stopwatch.Frequency;
                VrLog.Info("TEST 504: '" + h.ItemId + "' on '" + h.Holster + "': its deepest point is "
                           + Cm(worst) + " cm in front of your back, " + Cm(worstUp) + " cm above the pelvis, "
                           + Cm(worstSide) + " cm to the side - " + (h.InBody ? "hidden (inside you)." : "shown.")
                           + " (holster_back_m / holster_body_half_m / holster_leg_m)");
            }

            return h.InBody;
        }

        private static string Cm(float m) => (m * 100f).ToString("F0", CultureInfo.InvariantCulture);

        /// <summary>TEST 503: this tick's pelvis-to-neck line, in world; TEST 504: the
        /// body's facing, side and up (the agent's frame - the hips').</summary>
        private static void Torso(Skeleton skeleton, MatrixFrame agentFrame)
        {
            if (skeleton.Pointer != _torsoSkeleton)
            {
                _torsoSkeleton = skeleton.Pointer;
                _pelvis = _neck = -1;
                int count = skeleton.GetBoneCount();
                for (sbyte b = 0; b < count; b++)
                {
                    string name = skeleton.GetBoneName(b);
                    if (name == "pelvis")
                        _pelvis = b;
                    else if (name == "neck")
                        _neck = b;
                    else if (name == "l_upperarm_twist")
                        _lShoulder = b;
                    else if (name == "r_upperarm_twist")
                        _rShoulder = b;
                }
            }

            _haveTorso = _pelvis >= 0 && _neck >= 0;
            if (!_haveTorso)
                return;

            _torsoA = agentFrame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(_pelvis).origin);
            _torsoB = agentFrame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(_neck).origin);
            _bodyFwd = agentFrame.rotation.f.NormalizedCopy();
            _bodySide = agentFrame.rotation.s.NormalizedCopy();
            _bodyUp = agentFrame.rotation.u.NormalizedCopy();

            // TEST 506: the chest's own facing, from the shoulders - the head-driven
            // body turns and bends it away from the hips'.
            _chestFwd = _bodyFwd;
            _chestSide = _bodySide;
            if (_lShoulder < 0 || _rShoulder < 0)
                return;

            Vec3 up = (_torsoB - _torsoA).NormalizedCopy();
            Vec3 across = agentFrame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(_rShoulder).origin)
                          - agentFrame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(_lShoulder).origin);
            across -= up * Vec3.DotProduct(across, up);
            if (across.Length < 0.05f)
                return;

            _chestSide = across.NormalizedCopy() * (Vec3.DotProduct(across, _bodySide) >= 0f ? 1f : -1f);
            Vec3 fwd = Vec3.CrossProduct(up, _chestSide).NormalizedCopy();
            _chestFwd = Vec3.DotProduct(fwd, _bodyFwd) >= 0f ? fwd : -fwd;
        }

        /// <summary>The box around all of a metamesh's meshes, in its own space
        /// (VrBlade measures meshes the same way).</summary>
        private static void Box(MetaMesh meta, out Vec3 lo, out Vec3 hi)
        {
            lo = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            hi = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            int n = meta.MeshCount;
            for (int i = 0; i < n && i < 32; i++)
            {
                Mesh mesh = meta.GetMeshAtIndex(i);
                if (mesh == null)
                    continue;
                lo = Vec3.Vec3Min(lo, mesh.GetBoundingBoxMin());
                hi = Vec3.Vec3Max(hi, mesh.GetBoundingBoxMax());
            }
        }

        /// <summary>A metamesh's size: its box's diagonal.</summary>
        internal static float Size(MetaMesh meta)
        {
            Box(meta, out Vec3 lo, out Vec3 hi);
            return hi.x >= lo.x ? (hi - lo).Length : 0f;
        }

        internal static MetaMesh Largest(List<MetaMesh> metas)
        {
            MetaMesh best = null;
            float size = -1f;
            foreach (MetaMesh meta in metas)
            {
                float s = Size(meta);
                if (s > size)
                {
                    size = s;
                    best = meta;
                }
            }
            return best;
        }

        /// <summary>TEST 412: the look a slot hangs by. By size alone, a quiver's
        /// bare arrow (arrow_bl_b, 0.97 m) or flying arrow can out-measure the squat
        /// quiver it sits in - "the quiver sometimes spawns a long brown stick". So
        /// the item's own holster look by name first, then the largest that is not
        /// the bare or flying weapon, then the largest. Crafted items name no holster
        /// look (CraftedDataView), so swords and javelins keep the size rule.</summary>
        private static MetaMesh HolsterLook(ItemObject item, List<MetaMesh> metas, out string why)
        {
            foreach (string name in new[] { item.HolsterWithWeaponMeshName, item.HolsterMeshName })
            {
                MetaMesh named = string.IsNullOrEmpty(name) ? null : metas.Find(m => m.GetName() == name);
                if (named != null)
                {
                    why = "the holster look by name";
                    return named;
                }
            }

            List<MetaMesh> holsters = metas.FindAll(m =>
                m.GetName() != item.MultiMeshName && m.GetName() != item.FlyingMeshName);
            if (holsters.Count > 0 && holsters.Count < metas.Count)
            {
                why = "the largest that is not the bare or flying weapon";
                return Largest(holsters);
            }

            why = "the largest";
            return Largest(metas);
        }

        internal static MetaMesh Smallest(List<MetaMesh> metas)
        {
            MetaMesh best = null;
            float size = float.MaxValue;
            foreach (MetaMesh meta in metas)
            {
                float s = Size(meta);
                if (s < size)
                {
                    size = s;
                    best = meta;
                }
            }
            return best;
        }

        internal static string Sizes(List<MetaMesh> metas)
        {
            var text = new StringBuilder();
            foreach (MetaMesh meta in metas)
                text.Append(text.Length > 0 ? ", " : "").Append("'").Append(meta.GetName()).Append("' ")
                    .Append(Size(meta).ToString("F2", CultureInfo.InvariantCulture)).Append(" m");
            return text.ToString();
        }

        private static List<MetaMesh> Metas(WeakGameEntity e)
        {
            var list = new List<MetaMesh>();
            int metas = e.MultiMeshComponentCount;
            for (int m = 0; m < metas && m < 8; m++)
            {
                MetaMesh meta = e.GetMetaMesh(m);
                if (meta != null)
                    list.Add(meta);
            }
            return list;
        }

        private static bool Same(MatrixFrame a, MatrixFrame b)
        {
            return (a.origin - b.origin).LengthSquared < 1e-10f
                   && (a.rotation.f - b.rotation.f).LengthSquared < 1e-10f
                   && (a.rotation.u - b.rotation.u).LengthSquared < 1e-10f;
        }

        private static void Show(int k, bool shown)
        {
            GameEntity entity = _entity[k];
            if (entity != null && entity.GetVisibilityExcludeParents() != shown)
                entity.SetVisibilityExcludeParents(shown);
        }

        private static void HideAll()
        {
            if (!ReferenceEquals(_viewMission, Mission.Current))
                return;
            for (int k = 0; k < Slots; k++)
                Show(k, false);
        }

        /// <summary>TEST 336: the engine's slot keeps the item's own meshes off
        /// only while our copy hangs. Each original is the same native MetaMesh,
        /// held alive by its wrapper.</summary>
        private static void TakeOff(Hung h, WeakGameEntity e, List<MetaMesh> metas, int k)
        {
            bool first = h.Originals.Count == 0;
            string masks = first ? Masks(metas) : null;

            foreach (MetaMesh meta in metas)
            {
                e.RemoveMultiMesh(meta);
                if (!h.Originals.Exists(o => o.Pointer == meta.Pointer))
                    h.Originals.Add(meta);
            }

            if (first)
            {
                VrLog.Info("TEST 336 [" + (EquipmentIndex)k + "] '" + h.ItemId + "': took the engine's own "
                           + metas.Count + " mesh(es) off its holstered entity (masks " + masks
                           + "; bit 1 = it was being drawn).");
            }
            else if (++h.ReAdds % 60 == 1)
            {
                VrLog.Info("TEST 336 [" + (EquipmentIndex)k + "] '" + h.ItemId + "': the engine put its meshes back "
                           + h.ReAdds + " time(s); taken off again.");
            }
        }

        private static void SetShown(MetaMesh copy, bool shown, ref bool state)
        {
            if (state == shown)
                return;
            state = shown;
            copy.SetVisibilityMask((VisibilityMaskFlags)(shown ? 33 : 0));
        }

        /// <summary>Every original back on the engine's slot entity: a draw has
        /// them to copy, a drop lies visible.</summary>
        private static void PutBack(Hung h, Agent agent, EquipmentIndex idx)
        {
            if (h.Originals.Count == 0)
                return;

            WeakGameEntity e = agent.GetWeaponEntityFromEquipmentSlot(idx);
            if (e.IsValid && e.Pointer == h.Source)
            {
                foreach (MetaMesh original in h.Originals)
                {
                    original.SetVisibilityMask((VisibilityMaskFlags)33);
                    VrSkinnedWeapon.Opaque(original);
                    e.AddMultiMesh(original, true);
                    original.SetVisibilityMask((VisibilityMaskFlags)33);
                }
            }

            h.Originals.Clear();
        }

        /// <summary>TEST 336: the engine is about to drop this slot - its own
        /// meshes go back first, or the ground item has none (DropPatch).</summary>
        internal static void RestoreSlot(Agent agent, EquipmentIndex slot)
        {
            int k = (int)slot;
            if (k >= 0 && k < Slots && _hung[k] != null && ReferenceEquals(_viewMission, Mission.Current))
                PutBack(_hung[k], agent, slot);
        }

        private static string Masks(List<MetaMesh> metas)
        {
            var text = new StringBuilder();
            foreach (MetaMesh meta in metas)
            {
                Mesh mesh = meta.MeshCount > 0 ? meta.GetMeshAtIndex(0) : null;
                text.Append(text.Length > 0 ? "," : "").Append(mesh != null ? ((int)mesh.VisibilityMask).ToString() : "-");
            }
            return text.ToString();
        }

        private static void Forget(int k, Agent agent = null)
        {
            Hung h = _hung[k];
            if (h != null && agent != null)
                PutBack(h, agent, (EquipmentIndex)k);

            _hung[k] = null;
            GameEntity entity = _entity[k];
            if (h == null || entity == null)
                return;

            foreach (MetaMesh copy in h.Copies)
                entity.RemoveMultiMesh(copy);
            if (h.LookFull != null)
                entity.RemoveMultiMesh(h.LookFull);
            if (h.LookEmpty != null)
                entity.RemoveMultiMesh(h.LookEmpty);
            entity.SetVisibilityExcludeParents(false);
        }

        /// <summary>Entities die with their scene (TEST 308: key on the Mission,
        /// never on the scene pointer, which arena reloads reuse).</summary>
        private static void NewMission()
        {
            if (ReferenceEquals(_viewMission, Mission.Current))
                return;

            _viewMission = Mission.Current;
            for (int k = 0; k < Slots; k++)
            {
                _hung[k] = null;
                _entity[k] = null;
            }
        }

        private static void Break(string what, Exception ex)
        {
            _broken = true;
            try
            {
                Agent agent = Mission.Current?.MainAgent;
                for (int k = 0; k < Slots; k++)
                {
                    Show(k, false);
                    if (_hung[k] != null && agent != null && ReferenceEquals(_viewMission, Mission.Current))
                        PutBack(_hung[k], agent, (EquipmentIndex)k);
                }
            }
            catch (Exception)
            {
            }
            VrLog.Error("TEST 328b: " + what + " failed; the holsters go back to the engine's own "
                        + "(invisible in first person). Nothing else is affected.", ex);
        }

        private static readonly bool Probe = VrConfig.Bool("holster_view_probe", false);
        private const int MaxLines = 40;

        private static readonly Dictionary<int, string> _seen = new Dictionary<int, string>();
        private static Mission _probeMission;
        private static int _lines;
        private static bool _agentDumped;

        private static void ProbeTick(Agent agent, MBAgentVisuals visuals, Skeleton skeleton)
        {
            if (!Probe || agent == null || visuals == null || skeleton == null)
                return;

            try
            {
                if (!ReferenceEquals(_probeMission, Mission.Current))
                {
                    _probeMission = Mission.Current;
                    _seen.Clear();
                    _lines = 0;
                    _agentDumped = false;
                }

                if (_lines >= MaxLines)
                    return;

                EquipmentIndex main = agent.GetPrimaryWieldedItemIndex();
                EquipmentIndex off = agent.GetOffhandWieldedItemIndex();

                // Only slots that hold an item, only 0-3: the accessor is an
                // unchecked native passthrough (bl2vr-native-accessors-unbounded).
                for (int k = 0; k < 4; k++)
                {
                    EquipmentIndex idx = (EquipmentIndex)k;
                    ItemObject item = agent.Equipment[idx].Item;
                    if (item == null)
                        continue;

                    WeakGameEntity e = agent.GetWeaponEntityFromEquipmentSlot(idx);
                    if (!e.IsValid)
                        continue;

                    bool wielded = idx == main || idx == off;
                    string sig = item.StringId + "|" + wielded + "|" + e.MultiMeshComponentCount + "|" + e.ChildCount;
                    if (_seen.TryGetValue(k, out string had) && had == sig)
                        continue;
                    _seen[k] = sig;

                    Dump(agent, visuals, skeleton, idx, item, e, wielded);
                }

                if (!_agentDumped)
                {
                    _agentDumped = true;
                    DumpAgentMeshes(visuals);
                }
            }
            catch (Exception ex)
            {
                _lines = MaxLines;
                VrLog.Error("TEST 328: the holster probe failed; it is off for this mission.", ex);
            }
        }

        private static void Dump(Agent agent, MBAgentVisuals visuals, Skeleton skeleton,
                                 EquipmentIndex idx, ItemObject item, WeakGameEntity e, bool wielded)
        {
            MatrixFrame entityGlobal = e.GetGlobalFrame();
            MatrixFrame agentGlobal = visuals.GetFrame();
            Vec3 rel = agentGlobal.TransformToLocal(entityGlobal.origin);

            Line("TEST 328 [" + idx + "] '" + item.StringId + "' " + item.ItemType
                 + (wielded ? " WIELDED" : " sheathed")
                 + "; holsters [" + string.Join(", ", item.ItemHolsters ?? new string[0]) + "]"
                 + ", holster mesh '" + item.HolsterMeshName + "', with weapon '" + item.HolsterWithWeaponMeshName
                 + "', shift (" + V(item.HolsterPositionShift) + "), item mesh '" + item.MultiMeshName + "'"
                 + "; entity '" + e.Name + "' at (" + V(rel) + ") in the agent's frame, visible "
                 + e.GetVisibilityExcludeParents() + ", " + e.MultiMeshComponentCount + " metamesh, "
                 + e.ChildCount + " child, " + e.ClothSimulatorComponentCount + " cloth.");

            int metas = e.MultiMeshComponentCount;
            for (int m = 0; m < metas && m < 6; m++)
            {
                MetaMesh meta = e.GetMetaMesh(m);
                if (meta == null)
                    continue;

                MatrixFrame metaWorld = entityGlobal.TransformToParent(meta.Frame);
                Line("TEST 328 [" + idx + "] meta " + m + " '" + meta.GetName() + "' frame ("
                     + V(meta.Frame.origin) + ") meshes: " + Meshes(meta) + "; " + Fit(agent, item, skeleton, agentGlobal, metaWorld));
            }

            int children = e.ChildCount;
            for (int c = 0; c < children && c < 6; c++)
            {
                WeakGameEntity child = e.GetChild(c);
                if (!child.IsValid)
                    continue;

                MatrixFrame childWorld = child.GetGlobalFrame();
                var text = new StringBuilder();
                int cm = child.MultiMeshComponentCount;
                for (int m = 0; m < cm && m < 4; m++)
                {
                    MetaMesh meta = child.GetMetaMesh(m);
                    if (meta != null)
                        text.Append('\'').Append(meta.GetName()).Append("' ").Append(Meshes(meta)).Append("; ");
                }

                Line("TEST 328 [" + idx + "] child " + c + " '" + child.Name + "' at ("
                     + V(agentGlobal.TransformToLocal(childWorld.origin)) + "), " + text + Fit(agent, item, skeleton, agentGlobal, childWorld));
            }
        }

        /// <summary>The best bone x holster frame for a world frame, searched over
        /// every bone and every holster the item lists.</summary>
        private static string Fit(Agent agent, ItemObject item, Skeleton skeleton,
                                  MatrixFrame agentGlobal, MatrixFrame world)
        {
            string[] holsters = item.ItemHolsters;
            if (holsters == null || holsters.Length == 0)
                return "no holsters to fit";

            int bones = skeleton.GetBoneCount();
            float best = float.MaxValue;
            string bestBone = null, bestHolster = null;
            float bestAngle = 0f;

            foreach (string name in holsters)
            {
                if (string.IsNullOrEmpty(name))
                    continue;

                int h = MBItem.GetItemHolsterIndex(name);
                if (h < 0)
                    continue;

                MatrixFrame holster = MBItem.GetHolsterFrameByIndex(h);
                for (sbyte b = 0; b < bones; b++)
                {
                    MatrixFrame at = agentGlobal.TransformToParent(
                        skeleton.GetBoneEntitialFrameWithIndex(b).TransformToParent(holster));
                    float d = (at.origin - world.origin).Length;
                    if (d < best)
                    {
                        best = d;
                        bestBone = skeleton.GetBoneName(b);
                        bestHolster = name;
                        bestAngle = Angle(at.rotation, world.rotation);
                    }
                }
            }

            return bestBone == null
                ? "no holster index resolved"
                : "best fit '" + bestHolster + "' on bone '" + bestBone + "': " + best.ToString("F3", CultureInfo.InvariantCulture)
                  + " m, " + bestAngle.ToString("F0", CultureInfo.InvariantCulture) + " deg";
        }

        /// <summary>The agent entity's own metameshes, once per mission: a
        /// scabbard drawn on the body rather than in the slot would show here.</summary>
        private static void DumpAgentMeshes(MBAgentVisuals visuals)
        {
            GameEntity entity = visuals.GetEntity();
            if (entity == null)
                return;

            var text = new StringBuilder();
            int metas = entity.MultiMeshComponentCount;
            for (int m = 0; m < metas && m < 24; m++)
            {
                MetaMesh meta = entity.GetMetaMesh(m);
                if (meta != null)
                    text.Append('\'').Append(meta.GetName()).Append("' ");
            }

            Line("TEST 328 agent entity: " + metas + " metamesh(es): " + text + "; " + entity.ChildCount + " child entit(ies).");
        }

        private static string Meshes(MetaMesh meta)
        {
            var text = new StringBuilder();
            int n = meta.MeshCount;
            for (int i = 0; i < n && i < 6; i++)
            {
                Mesh mesh = meta.GetMeshAtIndex(i);
                if (mesh != null)
                    text.Append(mesh.Name).Append("(mask ").Append((int)mesh.VisibilityMask).Append(") ");
            }
            return text.ToString();
        }

        private static float Angle(Mat3 a, Mat3 b)
        {
            // Rotation between the two, from the trace of a^T b. Both are taken
            // as orthonormal after normalising, which holster frames should be.
            a.Orthonormalize();
            b.Orthonormalize();
            float trace = Vec3.DotProduct(a.s, b.s) + Vec3.DotProduct(a.f, b.f) + Vec3.DotProduct(a.u, b.u);
            float c = Math.Max(-1f, Math.Min(1f, (trace - 1f) * 0.5f));
            return (float)(Math.Acos(c) * 180.0 / Math.PI);
        }

        private static void Line(string text)
        {
            _lines++;
            VrLog.Info(text);
        }

        private static string V(Vec3 v)
        {
            return v.x.ToString("F3", CultureInfo.InvariantCulture) + ", " + v.y.ToString("F3", CultureInfo.InvariantCulture)
                   + ", " + v.z.ToString("F3", CultureInfo.InvariantCulture);
        }
    }
}
