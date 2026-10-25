using System;
using System.Collections.Generic;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The blade as geometry: measured once per weapon, read every frame as a
    /// world-space segment from grip to tip.
    ///
    /// WHY GEOMETRY AND NOT A CONFIG NUMBER
    ///
    /// Reach lives in the mesh, not in the item data. WeaponLength is what the
    /// game uses for reach on ITS animation, and the number the item XML carries
    /// is not the same thing as where the tip is once the weapon is aligned in
    /// the hand (TEST 231's poses rotate and offset every weapon by hand). The
    /// honest answer is measured from the thing being drawn: the metameshes
    /// copied onto our own weapon entity, each mesh's bounding box through its
    /// metamesh's frame, unioned into one AABB in the entity's space.
    ///
    ///     longest axis  = the blade, grip end to tip
    ///     next axis     = how wide the blade is, for the ray thickness
    ///     grip end      = whichever end sits nearer the entity origin, which is
    ///                     the item bone - the palm - by construction
    ///
    /// Measured ONCE per weapon and cached; its frame is composed per tick. The
    /// cache is keyed by the item and by a stamp VrSkinnedWeapon bumps when it
    /// attaches or a pose is edited, so the live tuner cannot leave a stale
    /// segment behind. A model that measures oddly is what the blade_class_*
    /// overrides are for.
    ///
    /// THE FALLBACK IS THE ENGINE'S OWN ENTITY - see VrSkinnedWeapon for why the
    /// two paths exist. With weapon_skinned on (the tested configuration) the
    /// measured meshes are ours; with it off the same measurement runs against
    /// the engine's slot entity, whose frame the engine re-derives from the bone
    /// every tick.
    /// </summary>
    public static class VrBlade
    {
        /// <summary>A blade in world space. Grip and Tip are in the weapon
        /// entity's frame; the world points are the composition with World.</summary>
        public struct Segment
        {
            public bool Valid;
            public MatrixFrame World;
            public Vec3 Grip;
            public Vec3 Tip;
            public float Radius;

            /// <summary>TEST 311: an enemy blade from the item's attach frame,
            /// because its class has no fit yet.</summary>
            public bool FromFrame;

            public Vec3 GripWorld => World.TransformToParent(Grip);
            public Vec3 TipWorld => World.TransformToParent(Tip);

            public Vec3 AxisWorld
            {
                get
                {
                    Vec3 axis = TipWorld - GripWorld;
                    return axis.NormalizedCopy();
                }
            }

            public float Length => (Tip - Grip).Length;
        }

        /// <summary>A shield as one sphere: the centre of its plate and the
        /// radius that stands for the face (TEST 241). Measured from the same
        /// AABB the blade is; the shape is cached by item id because it cannot
        /// change, and only the frame is re-read per tick.</summary>
        public struct Shield
        {
            public bool Valid;
            public MatrixFrame World;
            public Vec3 Centre;
            public float Radius;

            public Vec3 CentreWorld => World.TransformToParent(Centre);
        }

        /// <summary>
        /// TEST 315: an enemy shield as the plate it is - an oriented box, for the
        /// blade to stop on (VrCollide) and to strike (VrPhysicalCombat). N is the
        /// face's normal, A and B the face's own axes, all unit; the halves are
        /// world metres. Bone is the bone the shield hangs from, for the blow.
        /// </summary>
        public struct Plate
        {
            public bool Valid;
            public Vec3 Centre;
            public Vec3 A;
            public Vec3 B;
            public Vec3 N;
            public float HalfA;
            public float HalfB;
            public float HalfN;
            public sbyte Bone;
            public EquipmentIndex Slot;
        }

        /// <summary>An item's plate in its own space: an oriented box, N the face's
        /// normal. TEST 315b: fitted to the engine's own shield collision body (the
        /// item's shield_body_name) - the mesh's AABB is no plate when the face is
        /// tilted in it (reinforced_kite_shield: 0.547 x 1.437 x 0.290 m).</summary>
        private sealed class PlateShape
        {
            public bool Valid;
            public Vec3 Centre;
            public Vec3 A;
            public Vec3 B;
            public Vec3 N;
            public float HalfA;
            public float HalfB;
            public float HalfN;
        }

        private static readonly Dictionary<string, PlateShape> _plates =
            new Dictionary<string, PlateShape>(StringComparer.Ordinal);

        /// <summary>auto, frame or bone: whether an enemy shield is placed by its
        /// attach bone times the item's own usage Frame (the item XML's position and
        /// rotation) or by the bone alone, as TEST 241-311 did. auto measures both
        /// against the engine's own shield-block contacts (ProbePlate).</summary>
        private static readonly string PlateFrameMode =
            VrConfig.String("combat_enemy_shield_frame", "auto").Trim().ToLowerInvariant();

        private const int PlateDecideAfter = 10;
        private static readonly double[] _plateSq = new double[2];   // [0] bone, [1] bone x Frame
        private static int _plateSamples;

        /// <summary>TEST 320: where the engine's contacts sit in each candidate
        /// plate's own frame - along A, along B, and off the face toward the
        /// attacker - summed, and summed squared. A steady mean is an offset to
        /// correct; a spread is the contacts landing all over the face.</summary>
        private static readonly Vec3[] _plateSum = new Vec3[2];
        private static readonly Vec3[] _plateSumSq = new Vec3[2];
        private static bool _plateUseFrame = true;

        /// <summary>What one shield item measures, in the item entity's space.
        /// Item-static, hence the cache.</summary>
        private sealed class ShieldShape
        {
            public bool Valid;
            public Vec3 Centre;
            public float Radius;
        }

        private static readonly Dictionary<string, ShieldShape> _shields =
            new Dictionary<string, ShieldShape>(StringComparer.Ordinal);

        /// <summary>What one weapon item measures, in its entity's space:
        /// grip, tip and radius - the enemy's version of the player's per-hand
        /// blade (TEST 239c). Item-static, so it is cached by item id; the
        /// frame it is placed with is the caller's (the attach bone).</summary>
        private sealed class WeaponShape
        {
            public bool Valid;
            public Vec3 Grip;
            public Vec3 Tip;
            public float Radius;
        }

        private static readonly Dictionary<string, WeaponShape> _shapes =
            new Dictionary<string, WeaponShape>(StringComparer.Ordinal);

        /// <summary>Used when a shield's model does not measure as a plate
        /// (TEST 241/242). The caller adds it to the item bone instead of a
        /// centre that would put the plate somewhere it is not.</summary>
        private static readonly float ShieldFallbackRadius =
            VrConfig.Float("combat_shield_fallback_radius", 0.30f);

        private const int Roles = 2;
        private const int MaxMeshes = 8;
        private const int MaxMeshParts = 8;
        private const float MinRadius = 0.010f;

        /// <summary>What was measured for one hand's weapon, cached until the
        /// item or the pose changes.</summary>
        private sealed class Cache
        {
            public string ItemId = string.Empty;
            public UIntPtr Source;
            public int Stamp;
            public bool Valid;

            public Vec3 Grip;
            public Vec3 Tip;
            public float Radius;
        }

        private static readonly Cache[] _cache = { new Cache(), new Cache() };
        private static bool _broken;

        /// <summary>
        /// The blade for one hand, or false when there is nothing to measure or
        /// nothing the tick can trust the frame of. Never throws: a weapon whose
        /// meshes cannot be measured simply does not hit, and the log says why.
        /// </summary>
        public static bool TryGet(Agent agent, VrHandSlot slot, out Segment segment)
        {
            segment = default(Segment);

            if (_broken || agent == null || !slot.Valid || slot.Equipment == EquipmentIndex.None)
                return false;

            try
            {
                MissionWeapon weapon = agent.Equipment[slot.Equipment];
                if (weapon.IsEmpty)
                    return false;

                WeaponComponentData usage = weapon.CurrentUsageItem;
                if (usage == null)
                    return false;

                string itemId = weapon.Item?.StringId ?? string.Empty;
                string weaponClass = usage.WeaponClass.ToString();

                MatrixFrame world;
                List<MetaMesh> meshes;
                int stamp;
                UIntPtr source = UIntPtr.Zero;

                if (VrSkinnedWeapon.TryGetVisual(slot.Role, out world, out meshes, out stamp,
                                                 out string visualItem))
                {
                    itemId = visualItem ?? itemId;
                }
                else
                {
                    WeakGameEntity engine = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                    if (!engine.IsValid)
                        return false;

                    world = engine.GetGlobalFrame();
                    meshes = null;
                    stamp = 0;
                    source = engine.Pointer;
                }

                Cache cache = _cache[(int)slot.Role];
                if (!cache.Valid || cache.Stamp != stamp || cache.Source != source
                    || !string.Equals(cache.ItemId, itemId, StringComparison.Ordinal))
                {
                    if (meshes == null)
                    {
                        meshes = EngineMeshes(agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment));
                        if (meshes.Count == 0)
                            return false;
                    }

                    Measure(cache, meshes, itemId, weaponClass, usage);
                    cache.Source = source;
                    cache.Stamp = stamp;
                }

                if (!cache.Valid)
                    return false;

                segment.Valid = true;
                segment.World = world;
                segment.Grip = cache.Grip;
                segment.Tip = cache.Tip;
                segment.Radius = cache.Radius;
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 237: measuring a blade failed; physical hits are off for "
                            + "this session. Melee goes back to the engine's own path.", ex);
                return false;
            }
        }

        /// <summary>
        /// The AABB of every mesh on one entity, in that entity's space, then the
        /// blade read out of it, cached and logged once per weapon. The meshes'
        /// own boxes are in mesh space, so each corner goes through its
        /// metamesh's frame first.
        /// </summary>
        private static void Measure(Cache cache, List<MetaMesh> meshes, string itemId,
                                    string weaponClass, WeaponComponentData usage)
        {
            cache.Valid = false;
            cache.ItemId = itemId;

            if (!Bounds(meshes, out Vec3 min, out Vec3 max))
                return;

            BladeFromBounds(min, max, weaponClass,
                            out Vec3 grip, out Vec3 tip, out float radius,
                            out Vec3 size, out string overrideNote);

            cache.Grip = grip;
            cache.Tip = tip;
            cache.Radius = radius;
            cache.Valid = true;
            cache.ItemId = itemId;

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 237 blade: '{0}' ({1}) measures {2:F3} m from grip to tip and {3:F3} m "
                + "radius, in the weapon entity's space. The item's own length field is {4} "
                + "and its mesh AABB is ({5:F3},{6:F3},{7:F3}) m.{8}",
                itemId, weaponClass, (tip - grip).Length, radius,
                usage != null ? usage.WeaponLength : 0,
                size.x, size.y, size.z, overrideNote));
        }

        /// <summary>One entity's meshes as an AABB in that entity's space. The
        /// meshes' own boxes are in mesh space, so each corner goes through its
        /// metamesh's frame first.</summary>
        internal static bool Bounds(List<MetaMesh> meshes, out Vec3 min, out Vec3 max)
        {
            bool any = false;
            min = default(Vec3);
            max = default(Vec3);

            for (int m = 0; m < meshes.Count && m < MaxMeshes; m++)
            {
                MetaMesh meta = meshes[m];
                if (meta == null || !meta.IsValid)
                    continue;

                MatrixFrame frame = meta.Frame;
                int parts = meta.MeshCount;

                for (int p = 0; p < parts && p < MaxMeshParts; p++)
                {
                    Mesh mesh = meta.GetMeshAtIndex(p);
                    if (mesh == null)
                        continue;

                    Vec3 lo = mesh.GetBoundingBoxMin();
                    Vec3 hi = mesh.GetBoundingBoxMax();

                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vec3 point = new Vec3(
                            (corner & 1) == 0 ? lo.x : hi.x,
                            (corner & 2) == 0 ? lo.y : hi.y,
                            (corner & 4) == 0 ? lo.z : hi.z);

                        Vec3 world = frame.TransformToParent(point);

                        if (!any)
                        {
                            min = world;
                            max = world;
                            any = true;
                        }
                        else
                        {
                            min = Vec3.Vec3Min(min, world);
                            max = Vec3.Vec3Max(max, world);
                        }
                    }
                }
            }

            return any;
        }

        /// <summary>The blade read out of an AABB: longest axis, grip nearest the
        /// entity origin, thin dimension for the radius.</summary>
        private static void BladeFromBounds(Vec3 min, Vec3 max, string weaponClass,
                                            out Vec3 grip, out Vec3 tip, out float radius,
                                            out Vec3 size, out string overrideNote)
        {
            size = max - min;

            int axis = 0;
            if (Comp(size, 1) > Comp(size, axis)) axis = 1;
            if (Comp(size, 2) > Comp(size, axis)) axis = 2;

            // The blade proper is the THIN dimension - the next axis out is the
            // crossguard on a sword, and using that made the ray 10 cm fat
            // (TEST 237: 0.097 m measured against a 0.195 m guard).
            float thin = Math.Min(Comp(size, (axis + 1) % 3), Comp(size, (axis + 2) % 3));
            radius = Math.Max(MinRadius, thin * 0.5f);

            // The grip is the end of the long axis nearer the entity origin -
            // the item bone, which is the palm. A pommel behind the hand reads
            // correctly because "nearer" is signed.
            float minC = Comp(min, axis);
            float maxC = Comp(max, axis);
            float gripCoord = Math.Abs(minC) <= Math.Abs(maxC) ? minC : maxC;
            float tipCoord = gripCoord == minC ? maxC : minC;

            Vec3 centre = (min + max) * 0.5f;
            grip = SetComp(centre, axis, gripCoord);
            tip = SetComp(centre, axis, tipCoord);

            // blade_class_<WeaponClass>_length|radius|grip: the escape hatch for a
            // model whose AABB does not match the blade - a pennon, a banner, a
            // mesh with the shield boss in it. In metres, along the long axis.
            float overrideLength = VrConfig.Float("blade_class_" + weaponClass + "_length", 0f);
            float overrideRadius = VrConfig.Float("blade_class_" + weaponClass + "_radius", 0f);
            float overrideGrip = VrConfig.Float("blade_class_" + weaponClass + "_grip", 0f);

            Vec3 unit = Unit(axis);

            if (overrideLength > 0f)
                tip = grip + unit * overrideLength;

            if (overrideRadius > 0f)
                radius = overrideRadius;

            if (overrideGrip != 0f)
            {
                grip += unit * overrideGrip;
                tip += unit * overrideGrip;
            }

            overrideNote = overrideLength > 0f || overrideRadius > 0f || overrideGrip != 0f
                ? " Overrides applied: length=" + overrideLength.ToString("0.000", CultureInfo.InvariantCulture)
                  + ", radius=" + overrideRadius.ToString("0.000", CultureInfo.InvariantCulture)
                  + ", grip=" + overrideGrip.ToString("0.000", CultureInfo.InvariantCulture) + "."
                : string.Empty;
        }

        /// <summary>The engine's own entity's meshes (and cloth meshes, TEST 232),
        /// for when the skinned path is off. Built on demand, not per tick.</summary>
        private static List<MetaMesh> EngineMeshes(WeakGameEntity source)
        {
            var list = new List<MetaMesh>(MaxMeshes);

            int count = source.MultiMeshComponentCount;
            for (int i = 0; i < count && i < MaxMeshes; i++)
            {
                MetaMesh meta = source.GetMetaMesh(i);
                if (meta != null)
                    list.Add(meta);
            }

            int cloths = source.ClothSimulatorComponentCount;
            for (int i = 0; i < cloths && i < MaxMeshes; i++)
            {
                ClothSimulatorComponent cloth = source.GetClothSimulator(i);
                MetaMesh meta = cloth != null ? cloth.GetFirstMetaMesh() : null;
                if (meta != null)
                    list.Add(meta);
            }

            return list;
        }

        internal static float Comp(Vec3 v, int axis)
        {
            return axis == 0 ? v.x : (axis == 1 ? v.y : v.z);
        }

        internal static Vec3 SetComp(Vec3 v, int axis, float value)
        {
            return axis == 0 ? new Vec3(value, v.y, v.z)
                 : axis == 1 ? new Vec3(v.x, value, v.z)
                             : new Vec3(v.x, v.y, value);
        }

        internal static Vec3 Unit(int axis)
        {
            return axis == 0 ? new Vec3(1f, 0f, 0f)
                 : axis == 1 ? new Vec3(0f, 1f, 0f)
                             : new Vec3(0f, 0f, 1f);
        }

        /// <summary>
        /// The player's shield, measured from the same entity frame the blade
        /// uses (TEST 241). False when the slot holds no shield or when the
        /// model does not measure like a plate; the caller then has no shield
        /// to guard with, which is better than a guard made of an odd AABB.
        /// </summary>
        public static bool TryGetShield(Agent agent, VrHandSlot slot, out Shield shield)
        {
            shield = default(Shield);

            if (_broken || agent == null || !slot.Valid || slot.Equipment == EquipmentIndex.None)
                return false;

            try
            {
                MissionWeapon weapon = agent.Equipment[slot.Equipment];
                if (weapon.IsEmpty || !IsShield(weapon.CurrentUsageItem))
                    return false;

                MatrixFrame world;
                List<MetaMesh> meshes;

                if (VrSkinnedWeapon.TryGetVisual(slot.Role, out world, out meshes, out int _,
                                                 out string _))
                {
                    // The frame and meshes the player actually sees.
                }
                else
                {
                    WeakGameEntity engine = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                    if (!engine.IsValid)
                        return false;

                    world = engine.GetGlobalFrame();
                    meshes = EngineMeshes(engine);
                }

                ShieldShape shape = ShapeFor("entity:" + (weapon.Item?.StringId ?? string.Empty),
                                             weapon.Item?.StringId ?? string.Empty, meshes);
                if (shape == null || !shape.Valid)
                    return false;

                shield.Valid = true;
                shield.World = world;
                shield.Centre = shape.Centre;
                shield.Radius = shape.Radius;
                return true;
            }
            catch (Exception ex)
            {
                if (!_shieldWarned)
                {
                    _shieldWarned = true;
                    VrLog.Warn("TEST 241: measuring a shield failed once; that shield will "
                               + "not guard until it measures. " + ex.Message);
                }

                return false;
            }
        }

        /// <summary>
        /// Another agent's shield (TEST 241). The frame is the bone the engine
        /// attaches the item to - the same answer Mission.CreateMeleeBlow
        /// uses - because an NPC's slot entity is parked at the agent's feet
        /// (TEST 208) and its frame is not where the shield is drawn.
        ///
        /// A shape that does not measure like a plate falls back to the item
        /// bone itself: a raised shield's hand is at chest level and the
        /// caller adds a fallback radius. That is honest about what is known
        /// and never refuses a guard because a model measured oddly.
        /// </summary>
        public static bool TryGetShieldForAgent(Agent agent, EquipmentIndex slot, out Shield shield)
        {
            shield = default(Shield);

            if (_broken || agent == null || slot == EquipmentIndex.None)
                return false;

            try
            {
                MissionWeapon weapon = agent.Equipment[slot];
                if (weapon.IsEmpty || !IsShield(weapon.CurrentUsageItem))
                    return false;

                sbyte bone = -1;
                if (agent.Monster != null)
                {
                    bone = agent.Monster.GetBoneToAttachForItemFlags(weapon.Item.ItemFlags);

                    if (bone < 0)
                        bone = agent.Monster.OffHandItemBoneIndex;
                }

                if (bone < 0)
                    return false;

                // WORLD, not entitial: VrArmBones states entitial space is the
                // agent entity's own frame, so the entity's global frame must
                // be composed on - the same way VrWeaponHands places the
                // player's weapon. The TEST 239 probe caught this as an attach
                // bone 832 m from the impact (TEST 239b).
                // TryBoneWorld bounds the bone against the skeleton's own count: this
                // was the one unbounded GetBoneEntitialFrameWithIndex left on the
                // enemy path (2026-10-01 crash, rcx 27, VrPhysicalCombat.Tick).
                if (!TryBoneWorld(agent, bone, out MatrixFrame world))
                    return false;

                // The plate, from the ITEM's own mesh (TEST 242): the agent's
                // slot entity is the batched gear and measures the wrong box.
                string itemId = weapon.Item?.StringId ?? string.Empty;
                ShieldShape shape = ItemShapeFor("item:" + itemId, itemId, weapon.Item);

                // A measurement that is not plate-shaped (a batched box slipped
                // through, or a model with a banner) falls back to the item bone
                // with the fallback radius - which is what the chest band and
                // the guard action need anyway.
                bool usable = shape != null && shape.Valid
                              && shape.Radius > 0.05f && shape.Radius <= 0.5f
                              && shape.Centre.Length <= 0.8f;

                shield.Valid = true;
                shield.World = world;
                shield.Centre = usable ? shape.Centre : Vec3.Zero;
                shield.Radius = usable ? shape.Radius : ShieldFallbackRadius;
                return true;
            }
            catch (Exception ex)
            {
                if (!_enemyShieldWarned)
                {
                    _enemyShieldWarned = true;
                    VrLog.Warn("TEST 241: measuring an enemy shield failed once; that shield "
                               + "will not block. " + ex.Message);
                }

                return false;
            }
        }

        /// <summary>TEST 315: the shield an agent holds in a hand, as a plate. False
        /// for no wielded shield, or a model that does not measure like a plate.</summary>
        public static bool TryGetEnemyPlate(Agent agent, out Plate plate)
        {
            bool frame = PlateFrameMode == "frame" || (PlateFrameMode != "bone" && _plateUseFrame);

            // TEST 421: one read per agent per tick. VrCollide (the hand drive) and
            // the hit test (after it) each asked once per hand - same pattern as
            // VrPhysicalCombat.BodyOf, keyed on the mission clock.
            if (agent == null)
                return TryPlate(agent, frame, out plate);

            float stamp = Mission.Current != null ? Mission.Current.CurrentTime : 0f;
            for (int i = 0; i < _plateTick.Length; i++)
            {
                PlateTick hit = _plateTick[i];
                if (hit.Agent == agent.Index && hit.Stamp == stamp && hit.Frame == frame)
                {
                    plate = hit.Plate;
                    return hit.Ok;
                }
            }

            bool ok = TryPlate(agent, frame, out plate);
            _plateTick[_nextPlateTick] = new PlateTick
            {
                Agent = agent.Index, Stamp = stamp, Frame = frame, Ok = ok, Plate = plate,
            };
            _nextPlateTick = (_nextPlateTick + 1) % _plateTick.Length;
            return ok;
        }

        /// <summary>
        /// TEST 642: the player's own shield as a plate, where it is DRAWN - the
        /// skinned copy's frame times the copy's tuned pose, which is the item's
        /// own space, the one MeasurePlate fits the collision body in. It is last
        /// tick's drawing: the main hand resolves before the off hand is placed.
        /// </summary>
        internal static bool TryGetOwnPlate(Agent player, out Plate plate)
        {
            plate = default(Plate);
            if (_broken || player == null)
                return false;

            try
            {
                EquipmentIndex slot = player.GetOffhandWieldedItemIndex();
                if (slot == EquipmentIndex.None)
                    return false;

                MissionWeapon weapon = player.Equipment[slot];
                string itemId = weapon.Item?.StringId ?? string.Empty;
                if (weapon.IsEmpty || !IsShield(weapon.CurrentUsageItem) || itemId.Length == 0)
                    return false;

                if (!VrSkinnedWeapon.TryGetVisual(HandRole.Off, out MatrixFrame world, out List<MetaMesh> meshes,
                                                  out int _, out string _)
                    || meshes == null || meshes.Count == 0)
                    return false;

                if (!_plates.TryGetValue(itemId, out PlateShape shape))
                {
                    shape = MeasurePlate(itemId, weapon.Item);
                    _plates[itemId] = shape;
                }

                if (!shape.Valid)
                    return false;

                plate = PlateIn(world.TransformToParent(meshes[0].Frame), shape);
                plate.Bone = -1;
                plate.Slot = slot;
                return plate.Valid;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private struct PlateTick
        {
            public int Agent;
            public float Stamp;
            public bool Frame;
            public bool Ok;
            public Plate Plate;
        }

        private static readonly PlateTick[] _plateTick = InitPlateTick();
        private static int _nextPlateTick;

        private static PlateTick[] InitPlateTick()
        {
            var ring = new PlateTick[16];
            for (int i = 0; i < ring.Length; i++)
                ring[i].Agent = -1;
            return ring;
        }

        private static bool TryPlate(Agent agent, bool withFrame, out Plate plate)
        {
            plate = default(Plate);

            if (_broken || agent == null || !agent.IsHuman)
                return false;

            try
            {
                EquipmentIndex slot = agent.GetOffhandWieldedItemIndex();
                if (slot == EquipmentIndex.None || !IsShield(agent.Equipment[slot].CurrentUsageItem))
                    slot = agent.GetPrimaryWieldedItemIndex();

                if (slot == EquipmentIndex.None)
                    return false;

                MissionWeapon weapon = agent.Equipment[slot];
                WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
                if (!IsShield(usage) || agent.Monster == null)
                    return false;

                sbyte bone = agent.Monster.GetBoneToAttachForItemFlags(weapon.Item.ItemFlags);
                if (bone < 0)
                    bone = agent.Monster.OffHandItemBoneIndex;

                if (bone < 0 || !TryBoneWorld(agent, bone, out MatrixFrame world))
                    return false;

                string itemId = weapon.Item?.StringId ?? string.Empty;
                if (itemId.Length == 0)
                    return false;

                if (!_plates.TryGetValue(itemId, out PlateShape shape))
                {
                    shape = MeasurePlate(itemId, weapon.Item);
                    _plates[itemId] = shape;
                }

                if (!shape.Valid)
                    return false;

                if (withFrame)
                    world = world.TransformToParent(usage.Frame);

                plate = PlateIn(world, shape);
                plate.Bone = bone;
                plate.Slot = slot;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The item-space box through a world frame. A bone frame carries
        /// the agent's scale, so the axes are measured, then made unit.</summary>
        private static Plate PlateIn(MatrixFrame world, PlateShape shape)
        {
            Vec3 a = world.rotation.TransformToParent(shape.A);
            Vec3 b = world.rotation.TransformToParent(shape.B);
            Vec3 n = world.rotation.TransformToParent(shape.N);

            float la = a.Length;
            float lb = b.Length;
            float ln = n.Length;

            return new Plate
            {
                Valid = la > 1e-4f && lb > 1e-4f && ln > 1e-4f,
                Centre = world.TransformToParent(shape.Centre),
                A = a * (1f / Math.Max(la, 1e-4f)),
                B = b * (1f / Math.Max(lb, 1e-4f)),
                N = n * (1f / Math.Max(ln, 1e-4f)),
                HalfA = shape.HalfA * la,
                HalfB = shape.HalfB * lb,
                HalfN = shape.HalfN * ln,
            };
        }

        /// <summary>
        /// TEST 315b: a shield's plate from the engine's own shield collision body
        /// (ItemObject.CollisionBodyName, the XML's shield_body_name - what
        /// MissionWeapon hands the native side as the weapon's CollisionShape):
        /// its triangles, capsules and spheres, fitted with an oriented box
        /// (FitPlate). The mesh AABB is only the fallback, and a thick one is
        /// capped to a thin face.
        /// </summary>
        private static PlateShape MeasurePlate(string itemId, ItemObject item)
        {
            var points = new List<Vec3>(64);
            float round = 0f;
            int triangles = 0;
            int capsules = 0;
            string body = item?.CollisionBodyName;

            try
            {
                PhysicsShape physics = string.IsNullOrEmpty(body) ? null : PhysicsShape.GetFromResource(body, true);
                if (physics != null)
                {
                    var tri = new Vec3[3];
                    for (int m = 0; m < physics.TriangleMeshCount(); m++)
                    {
                        int count = physics.TriangleCountInTriangleMesh(m);
                        for (int t = 0; t < count && points.Count < 3000; t++)
                        {
                            physics.GetTriangle(tri, m, t);
                            points.Add(tri[0]);
                            points.Add(tri[1]);
                            points.Add(tri[2]);
                            triangles++;
                        }
                    }

                    var capsule = new CapsuleData();
                    for (int c = 0; c < physics.CapsuleCount(); c++)
                    {
                        physics.GetCapsule(ref capsule, c);
                        points.Add(capsule.P1);
                        points.Add(capsule.P2);
                        round = Math.Max(round, capsule.Radius);
                        capsules++;
                    }

                    var sphere = new SphereData();
                    for (int c = 0; c < physics.SphereCount(); c++)
                    {
                        physics.GetSphere(ref sphere, c);
                        points.Add(sphere.Origin);
                        round = Math.Max(round, sphere.Radius);
                        capsules++;
                    }
                }
            }
            catch (Exception)
            {
                points.Clear();
            }

            var shape = new PlateShape();
            string from;

            if (points.Count >= 3 && FitPlate(points, out shape.Centre, out shape.A, out shape.B, out shape.N,
                                              out shape.HalfA, out shape.HalfB, out shape.HalfN))
            {
                shape.HalfA += round;
                shape.HalfB += round;
                shape.HalfN += round;
                from = string.Format(CultureInfo.InvariantCulture, "its collision body '{0}' ({1} triangles, {2} "
                                     + "capsules/spheres)", body, triangles, capsules);
            }
            else if (Bounds(ItemMeshes(item), out Vec3 min, out Vec3 max))
            {
                Vec3 size = max - min;
                int thin = 0;
                for (int k = 1; k < 3; k++)
                {
                    if (size[k] < size[thin])
                        thin = k;
                }

                int a = (thin + 1) % 3;
                int b = (thin + 2) % 3;
                shape.Centre = (min + max) * 0.5f;
                shape.A = Unit(a);
                shape.B = Unit(b);
                shape.N = Unit(thin);
                shape.HalfA = size[a] * 0.5f;
                shape.HalfB = size[b] * 0.5f;
                shape.HalfN = Math.Min(size[thin] * 0.5f, 0.03f);
                from = string.Format(CultureInfo.InvariantCulture, "its mesh box ({0:F3},{1:F3},{2:F3}) - no collision "
                                     + "body '{3}' to read", size.x, size.y, size.z, body ?? "");
            }
            else
            {
                VrLog.Info("TEST 315 plate: '" + itemId + "' has neither a collision body nor a mesh to measure; "
                           + "the blade passes it, ShieldStops still blocks.");
                return shape;
            }

            float face = Math.Min(shape.HalfA, shape.HalfB) * 2f;
            float length = Math.Max(shape.HalfA, shape.HalfB) * 2f;
            shape.Valid = face >= 0.15f && length <= 2f && shape.HalfN < Math.Min(shape.HalfA, shape.HalfB);

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 315 plate: '{0}' from {1}: face {2:F2} x {3:F2} m, {4:F3} m deep (thickness and curve); {5}.",
                itemId, from, length, face, shape.HalfN * 2f,
                shape.Valid ? "solid to the blade" : "not plate-like - the blade passes it, ShieldStops still blocks"));

            return shape;
        }

        /// <summary>
        /// An oriented box round a point cloud: the axes of its spread (power
        /// iteration on the covariance - the longest first, then the next, the
        /// normal across both), the centre and halves from the extents along them.
        /// Pure, for the tests.
        /// </summary>
        internal static bool FitPlate(List<Vec3> points, out Vec3 centre, out Vec3 a, out Vec3 b, out Vec3 n,
                                      out float halfA, out float halfB, out float halfN)
        {
            centre = a = b = n = Vec3.Zero;
            halfA = halfB = halfN = 0f;

            if (points == null || points.Count < 3)
                return false;

            Vec3 mean = Vec3.Zero;
            foreach (Vec3 p in points)
                mean += p;
            mean *= 1f / points.Count;

            var c = new double[3, 3];
            foreach (Vec3 p in points)
            {
                Vec3 d = p - mean;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        c[i, j] += d[i] * d[j];
            }

            if (!Principal(c, out Vec3 first))
                return false;

            // The next axis: the spread with the first taken out.
            double l1 = Rayleigh(c, first);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    c[i, j] -= l1 * first[i] * first[j];

            if (!Principal(c, out Vec3 second))
                return false;

            n = Vec3.CrossProduct(first, second);
            if (n.LengthSquared < 1e-8f)
                return false;

            n = n.NormalizedCopy();
            a = first;
            b = Vec3.CrossProduct(n, a).NormalizedCopy();

            float minA = float.MaxValue, maxA = float.MinValue;
            float minB = float.MaxValue, maxB = float.MinValue;
            float minN = float.MaxValue, maxN = float.MinValue;
            foreach (Vec3 p in points)
            {
                Vec3 d = p - mean;
                float pa = Vec3.DotProduct(d, a);
                float pb = Vec3.DotProduct(d, b);
                float pn = Vec3.DotProduct(d, n);
                minA = Math.Min(minA, pa); maxA = Math.Max(maxA, pa);
                minB = Math.Min(minB, pb); maxB = Math.Max(maxB, pb);
                minN = Math.Min(minN, pn); maxN = Math.Max(maxN, pn);
            }

            centre = mean + a * ((minA + maxA) * 0.5f) + b * ((minB + maxB) * 0.5f) + n * ((minN + maxN) * 0.5f);
            halfA = (maxA - minA) * 0.5f;
            halfB = (maxB - minB) * 0.5f;
            halfN = (maxN - minN) * 0.5f;
            return true;
        }

        /// <summary>The covariance's largest axis, by power iteration from its
        /// longest column.</summary>
        private static bool Principal(double[,] c, out Vec3 axis)
        {
            int best = 0;
            double bestLength = -1.0;
            for (int j = 0; j < 3; j++)
            {
                double length = c[0, j] * c[0, j] + c[1, j] * c[1, j] + c[2, j] * c[2, j];
                if (length > bestLength)
                {
                    bestLength = length;
                    best = j;
                }
            }

            double x = c[0, best], y = c[1, best], z = c[2, best];
            for (int k = 0; k < 64; k++)
            {
                double nx = c[0, 0] * x + c[0, 1] * y + c[0, 2] * z;
                double ny = c[1, 0] * x + c[1, 1] * y + c[1, 2] * z;
                double nz = c[2, 0] * x + c[2, 1] * y + c[2, 2] * z;
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length < 1e-12)
                    break;

                x = nx / length;
                y = ny / length;
                z = nz / length;
            }

            double size = Math.Sqrt(x * x + y * y + z * z);
            axis = size > 1e-9 ? new Vec3((float)(x / size), (float)(y / size), (float)(z / size), -1f) : Vec3.Zero;
            return size > 1e-9;
        }

        private static double Rayleigh(double[,] c, Vec3 v)
        {
            double sum = 0.0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    sum += v[i] * c[i, j] * v[j];
            return sum;
        }

        /// <summary>
        /// TEST 315: an engine shield block between two NPCs is the engine's own
        /// answer for where that shield is - the contact sits on its face. Both
        /// placements are scored by how far the contact misses the box; after
        /// PlateDecideAfter contacts, auto keeps the closer one.
        /// </summary>
        internal static void ProbePlate(Agent attacker, Agent victim, in AttackCollisionData data)
        {
            if (!data.AttackBlockedWithShield || data.CollidedWithShieldOnBack || data.IsMissile
                || victim == null || attacker == null || victim == Mission.Current?.MainAgent)
                return;

            if (!TryPlate(victim, false, out Plate bone) || !TryPlate(victim, true, out Plate frame))
                return;

            Vec3 contact = data.CollisionGlobalPosition;
            Vec3 eye = attacker.GetEyeGlobalPosition();
            float boneMiss = PlateMiss(contact, bone);
            float frameMiss = PlateMiss(contact, frame);

            _plateSq[0] += boneMiss * boneMiss;
            _plateSq[1] += frameMiss * frameMiss;
            Accumulate(0, contact, eye, bone);
            Accumulate(1, contact, eye, frame);
            _plateSamples++;

            bool decide = _plateSamples == PlateDecideAfter;
            if (decide && PlateFrameMode == "auto")
                _plateUseFrame = _plateSq[1] <= _plateSq[0];

            if (decide || (_plateSamples > PlateDecideAfter && _plateSamples % 50 == 0))
            {
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 315 plate: {0} engine shield blocks - the contact misses the plate by rms {1:F3} m "
                    + "placed bone x Frame, {2:F3} m on the bone alone -> using {3} (combat_enemy_shield_frame "
                    + "= {4}).",
                    _plateSamples, Math.Sqrt(_plateSq[1] / _plateSamples), Math.Sqrt(_plateSq[0] / _plateSamples),
                    (PlateFrameMode == "frame" || (PlateFrameMode != "bone" && _plateUseFrame)) ? "bone x Frame" : "the bone",
                    PlateFrameMode));

                int k = (PlateFrameMode == "frame" || (PlateFrameMode != "bone" && _plateUseFrame)) ? 1 : 0;
                Vec3 mean = _plateSum[k] * (1f / _plateSamples);
                Vec3 meanSq = _plateSumSq[k] * (1f / _plateSamples);
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 320 plate: in the plate's own frame the engine's contacts sit at mean ({0:F3}, {1:F3}) "
                    + "along the face (spread {2:F3}, {3:F3}) and {4:F3} m off it toward the attacker (spread "
                    + "{5:F3}). A steady off-face mean is an offset; a spread the size of the face is contacts "
                    + "landing all over it.",
                    mean.x, mean.y, Spread(mean.x, meanSq.x), Spread(mean.y, meanSq.y),
                    mean.z, Spread(mean.z, meanSq.z)));
            }
        }

        private static void Accumulate(int k, Vec3 contact, Vec3 eye, Plate plate)
        {
            Vec3 d = contact - plate.Centre;
            float side = Vec3.DotProduct(eye - plate.Centre, plate.N) >= 0f ? 1f : -1f;
            var local = new Vec3(Vec3.DotProduct(d, plate.A), Vec3.DotProduct(d, plate.B),
                                 side * Vec3.DotProduct(d, plate.N), -1f);

            _plateSum[k] += local;
            _plateSumSq[k] += new Vec3(local.x * local.x, local.y * local.y, local.z * local.z, -1f);
        }

        private static double Spread(float mean, float meanSq)
        {
            return Math.Sqrt(Math.Max(0f, meanSq - mean * mean));
        }

        /// <summary>How far a point lies outside a plate's box, in metres; 0 inside.
        /// Pure, for the tests.</summary>
        internal static float PlateMiss(Vec3 point, Plate plate)
        {
            Vec3 d = point - plate.Centre;
            float n = Math.Max(0f, Math.Abs(Vec3.DotProduct(d, plate.N)) - plate.HalfN);
            float a = Math.Max(0f, Math.Abs(Vec3.DotProduct(d, plate.A)) - plate.HalfA);
            float b = Math.Max(0f, Math.Abs(Vec3.DotProduct(d, plate.B)) - plate.HalfB);
            return (float)Math.Sqrt(n * n + a * a + b * b);
        }

        private static bool _enemyShieldWarned;
        private static bool _shieldWarned;
        private static bool _enemyShapeWarned;

        /// <summary>
        /// An enemy's weapon as a WHOLE blade in its own entity's space
        /// (TEST 239c): the same AABB read the player's per-hand blade gets -
        /// longest axis, grip end nearer the origin, thin dimension for the
        /// radius - measured on the engine's slot entity and cached by item id.
        /// The caller composes the world frame (the attach bone) and decides
        /// whether the result is trustworthy by putting the engine's own
        /// contact point through it.
        ///
        /// TEST 238e measured this same AABB and got fat radii and nonsense
        /// distances, because the entity frame it used was parked at the
        /// agent's feet; the shape itself was never the fault. It is only ever
        /// used when the contact validates it, so a batched-mesh oddity falls
        /// back to the hand-to-impact line instead of blocking wrongly.
        /// </summary>
        public static bool TryGetWeaponShape(Agent agent, EquipmentIndex slot,
                                             out Vec3 grip, out Vec3 tip, out float radius)
        {
            grip = Vec3.Zero;
            tip = Vec3.Zero;
            radius = 0f;

            if (_broken || agent == null || slot == EquipmentIndex.None)
                return false;

            try
            {
                MissionWeapon weapon = agent.Equipment[slot];
                if (weapon.IsEmpty)
                    return false;

                WeaponComponentData usage = weapon.CurrentUsageItem;
                if (usage == null || !usage.IsMeleeWeapon)
                    return false;

                string itemId = weapon.Item?.StringId ?? string.Empty;

                string key = "item:" + itemId;
                if (itemId.Length != 0 && _shapes.TryGetValue(key, out WeaponShape cached))
                {
                    if (!cached.Valid)
                        return false;

                    grip = cached.Grip;
                    tip = cached.Tip;
                    radius = cached.Radius;
                    return true;
                }

                var shape = new WeaponShape();
                List<MetaMesh> meshes = ItemMeshes(weapon.Item);

                if (Bounds(meshes, out Vec3 min, out Vec3 max))
                {
                    BladeFromBounds(min, max, usage.WeaponClass.ToString(),
                                    out shape.Grip, out shape.Tip, out shape.Radius,
                                    out Vec3 _, out string _);
                    shape.Valid = true;
                }

                if (itemId.Length != 0)
                    _shapes[key] = shape;

                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "TEST 242 weapon: '{0}' ({1}) measures {2:F3} m from grip to tip and "
                    + "{3:F3} m radius in its item mesh's space; {4}.",
                    itemId.Length == 0 ? "?" : itemId, usage.WeaponClass,
                    (shape.Tip - shape.Grip).Length, shape.Radius,
                    shape.Valid ? "used when the blow's contact validates it" : "unusable"));

                if (!shape.Valid)
                    return false;

                grip = shape.Grip;
                tip = shape.Tip;
                radius = shape.Radius;
                return true;
            }
            catch (Exception ex)
            {
                if (!_enemyShapeWarned)
                {
                    _enemyShapeWarned = true;
                    VrLog.Warn("TEST 239c: measuring an enemy weapon failed once; that blow "
                               + "falls back to the hand-to-impact line. " + ex.Message);
                }

                return false;
            }
        }

        /// <summary>
        /// A bone's frame in WORLD space (moved here from VrPhysicalCombat, TEST
        /// 262, so the enemy-blade fit and its probe can share it without a
        /// second copy). Agent.GetBoneEntitialFrame / Skeleton.GetBoneEntitial-
        /// FrameWithIndex return the bone in the agent ENTITY's own frame -
        /// world = the entity's global frame times the entitial frame, the same
        /// composition VrWeaponHands places the weapon with.
        /// </summary>
        /// <summary>Logged once: a refused bone index is a fixed property of a rig,
        /// so it would otherwise repeat every frame of every mounted fight.</summary>
        private static bool _boneRangeLogged;

        internal static bool TryBoneWorld(Agent agent, sbyte bone, out MatrixFrame world)
        {
            world = MatrixFrame.Identity;

            if (agent == null || bone < 0)
                return false;

            try
            {
                MBAgentVisuals visuals = agent.AgentVisuals;
                Skeleton skeleton = visuals != null ? visuals.GetSkeleton() : null;
                GameEntity entity = visuals != null ? visuals.GetEntity() : null;

                if (skeleton == null || entity == null)
                    return false;

                // THE BOUND, AND IT IS WHAT CRASHED THE GAME TWICE ON 2026-09-29.
                //
                // GetBoneEntitialFrameWithIndex is an unbounded native accessor: it
                // indexes the skeleton's bone array with whatever it is given and
                // dereferences the result. An index past the end reads a pointer out
                // of neighbouring heap and follows it, which lands as
                //
                //   ACCESS VIOLATION at TaleWorlds.Native.dll+0x6FCC23 ... read 0x0
                //
                // with rcx holding the index. The catch below CANNOT save it - the
                // fault is a hardware access violation inside the engine, not a
                // managed exception, so it takes the process down with our handler
                // as the only trace. Both crashes read rcx 1B: bone 27.
                //
                // Why 27 exists at all: the index comes from a BLOW, which names a
                // bone on the rig the blow was authored against. Both crashes were
                // mounted melee, where the agent whose skeleton this asks is not
                // always the rig that numbered the bone - a horse has its own, much
                // shorter one - and an agent dying mid-frame swaps the skeleton under
                // an index that was valid when it was read.
                //
                // So: ask the skeleton how many bones it HAS. sbyte, and MaxBoneCount
                // is 64, so no cast games.
                sbyte bones = skeleton.GetBoneCount();
                if (bones <= 0 || bone >= bones)
                {
                    if (!_boneRangeLogged)
                    {
                        _boneRangeLogged = true;
                        VrLog.Warn($"Bone {bone} was asked of a skeleton that has {bones}; "
                                   + "refused. Unbounded is what this accessor is - it would "
                                   + "have read past the bone array and null-dereferenced "
                                   + "inside the engine, which no try/catch can hold. A blow "
                                   + "names a bone on the rig it was authored against, and "
                                   + "mounted combat asks it of a different one.");
                    }

                    return false;
                }

                world = entity.GetGlobalFrame().TransformToParent(
                    skeleton.GetBoneEntitialFrameWithIndex(bone));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // --- TEST 262: the enemy's blade, fitted from the engine's own contacts ---

        /// <summary>
        /// A running per-weapon-class fit of "where a contact lands in the
        /// attach bone's own local space, as a function of how far along the
        /// weapon the engine says it hit" (v ~= o + u*d, d = the engine's own
        /// CollisionDistanceOnWeapon). Built because the mesh-AABB
        /// reconstruction (TryGetWeaponShape) "usually does not validate" for an
        /// enemy: the item's authored mesh space is not necessarily the pose the
        /// engine attaches it to a bone in, and nothing manages that transform
        /// for anyone but the player's own hand (VrSkinnedWeapon). Rather than
        /// guess the transform, it is measured from real engine collisions - the
        /// same discipline as [[bl2vr-decompile-engine-api]]: read or measure,
        /// never assume.
        /// </summary>
        private sealed class EnemyBladeFit
        {
            public int Count;
            public double SumD, SumDD, SumVV;
            public Vec3 SumV;
            public Vec3 SumDV;

            public bool Ready;
            public Vec3 O, U;
            public float Rms;
            public int LastLoggedCount;
        }

        private static readonly Dictionary<WeaponClass, EnemyBladeFit> _enemyFits =
            new Dictionary<WeaponClass, EnemyBladeFit>();

        /// <summary>Samples a weapon class needs before TryGetEnemyBlade trusts
        /// the fit: below this, a hit near the grip and one near the tip are not
        /// yet separated from tracking noise.</summary>
        private const int EnemyFitMinSamples = 20;

        /// <summary>How many new samples between "TEST 262 fit" log lines.</summary>
        private const int EnemyFitLogEvery = 20;

        /// <summary>One real engine melee contact (MeleeHitPatch's probe). Pure
        /// accumulation: the fit itself is only solved on demand, so a battle's
        /// worth of NPC-vs-NPC blows costs a handful of additions each, not a
        /// matrix solve per hit.</summary>
        internal static void AccumulateEnemyBladeSample(WeaponClass weaponClass, Vec3 boneLocalContact,
                                                         float d)
        {
            if (!_enemyFits.TryGetValue(weaponClass, out EnemyBladeFit fit))
            {
                fit = new EnemyBladeFit();
                _enemyFits[weaponClass] = fit;
            }

            fit.Count++;
            fit.SumD += d;
            fit.SumDD += (double)d * d;
            fit.SumVV += boneLocalContact.LengthSquared;
            fit.SumV += boneLocalContact;
            fit.SumDV += boneLocalContact * d;
            fit.Ready = false;
        }

        /// <summary>Ordinary least squares, one axis at a time, from the sums
        /// alone. False when there are not yet enough samples, or the samples do
        /// not vary enough in d to fit a line (near-zero denominator - every hit
        /// landed at the same distance along the weapon, which a real fight does
        /// not do for long).</summary>
        private static bool SolveEnemyBladeFit(EnemyBladeFit fit)
        {
            if (fit.Count < EnemyFitMinSamples)
                return false;

            if (!SolveLine(fit.Count, fit.SumD, fit.SumDD, fit.SumVV, fit.SumV, fit.SumDV,
                           out Vec3 o, out Vec3 u, out float rms))
                return false;

            fit.O = o;
            fit.U = u;
            fit.Rms = rms;
            fit.Ready = true;
            return true;
        }

        /// <summary>
        /// Ordinary least squares, one axis at a time, from the running sums
        /// alone: v ~= o + u*d over n samples. Pure and Agent-free (tested
        /// directly by BannerlordVR.MathTests, unlike the fit it backs, which
        /// needs a live weapon and bone). False on a near-zero denominator -
        /// every sample at the same d, which cannot be separated into a line.
        /// </summary>
        private static bool SolveLine(int count, double sumD, double sumDD, double sumVV,
                                      Vec3 sumV, Vec3 sumDV, out Vec3 o, out Vec3 u, out float rms)
        {
            o = Vec3.Zero;
            u = Vec3.Zero;
            rms = 0f;

            double n = count;
            double denom = n * sumDD - sumD * sumD;
            if (Math.Abs(denom) < 1e-6)
                return false;

            float ux = (float)((n * sumDV.x - sumD * sumV.x) / denom);
            float uy = (float)((n * sumDV.y - sumD * sumV.y) / denom);
            float uz = (float)((n * sumDV.z - sumD * sumV.z) / denom);
            u = new Vec3(ux, uy, uz);

            o = new Vec3(
                (float)((sumV.x - ux * sumD) / n),
                (float)((sumV.y - uy * sumD) / n),
                (float)((sumV.z - uz * sumD) / n));

            double residualSq = sumVV
                - 2.0 * Vec3.DotProduct(o, sumV)
                - 2.0 * Vec3.DotProduct(u, sumDV)
                + n * o.LengthSquared
                + 2.0 * Vec3.DotProduct(o, u) * sumD
                + u.LengthSquared * sumDD;

            rms = (float)Math.Sqrt(Math.Max(0.0, residualSq) / n);
            return true;
        }

        /// <summary>
        /// The enemy's blade for one weapon slot (TEST 262), or false when the
        /// attach bone cannot be read. Segment.Grip/Tip are the fit's o and
        /// o + u*length in the attach bone's OWN local space; World is that
        /// bone's world frame this frame, composed exactly as TryGetShieldForAgent
        /// composes an enemy shield's. A class with no fit yet (see
        /// EnemyFitMinSamples) takes the item's attach frame (TEST 311, FromFrame).
        /// </summary>
        public static bool TryGetEnemyBlade(Agent agent, EquipmentIndex slot, out Segment segment)
        {
            segment = default(Segment);

            if (agent == null || slot == EquipmentIndex.None)
                return false;

            try
            {
                MissionWeapon weapon = agent.Equipment[slot];
                if (weapon.IsEmpty)
                    return false;

                WeaponComponentData usage = weapon.CurrentUsageItem;
                if (usage == null || !usage.IsMeleeWeapon)
                    return false;

                sbyte bone = agent.Monster != null
                    ? agent.Monster.GetBoneToAttachForItemFlags(weapon.Item.ItemFlags)
                    : (sbyte)(-1);

                if (bone < 0 || !TryBoneWorld(agent, bone, out MatrixFrame world))
                    return false;

                segment.Valid = true;
                segment.World = world;
                segment.Radius = EnemyBladeRadius;

                _enemyFits.TryGetValue(usage.WeaponClass, out EnemyBladeFit fit);
                if (fit != null && (fit.Ready || SolveEnemyBladeFit(fit)))
                {
                    segment.Grip = fit.O;
                    segment.Tip = fit.O + fit.U * usage.GetRealWeaponLength();
                    CompareFitToFrame(usage, fit);
                    return true;
                }

                // TEST 311: no fit yet for this class (20 engine contacts) - the
                // item's own attach frame instead, so the first exchanges of a
                // duel are tested blade to blade too.
                FrameBlade(usage.Frame, usage.WeaponLength * 0.01f,
                           out segment.Grip, out segment.Tip);
                segment.FromFrame = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>TEST 311: the blade the engine attaches - the item mesh runs
        /// from its origin along its own up axis, and the item's Frame
        /// (`&lt;Weapon position rotation&gt;`) puts that in the attach bone's space.
        /// The engine's GetRealWeaponLength reads the same axis (Frame.u). Pure,
        /// for the tests.</summary>
        internal static void FrameBlade(MatrixFrame frame, float length, out Vec3 grip, out Vec3 tip)
        {
            grip = frame.origin;
            tip = frame.origin + frame.rotation.u.NormalizedCopy() * length;
        }

        private static readonly HashSet<WeaponClass> _frameCompared = new HashSet<WeaponClass>();

        /// <summary>TEST 311: once per class, how far the fitted blade's axis is
        /// from the item frame's - near 0 says the frame fallback is the same
        /// blade the fit measured.</summary>
        private static void CompareFitToFrame(WeaponComponentData usage, EnemyBladeFit fit)
        {
            if (!_frameCompared.Add(usage.WeaponClass))
                return;

            FrameBlade(usage.Frame, usage.WeaponLength * 0.01f, out Vec3 grip, out Vec3 tip);
            Vec3 frameAxis = (tip - grip).NormalizedCopy();
            float cos = Vec3.DotProduct(fit.U.NormalizedCopy(), frameAxis);
            float deg = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, cos))) * 180.0 / Math.PI);

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 311 frame check {0}: the fitted blade is {1:0} deg from the item frame's "
                + "axis, its origin {2:F2} m from the frame's. Near 0 = the frame fallback "
                + "(used before a class has a fit) is the right blade.",
                usage.WeaponClass, deg, (fit.O - grip).Length));
        }

        /// <summary>Logs "TEST 262 fit" every EnemyFitLogEvery new samples, once
        /// the class has enough to fit at all. Called from MeleeHitPatch's probe,
        /// which already gates on combat_probe_enemy_blade.</summary>
        internal static void LogEnemyBladeFitIfDue(WeaponClass weaponClass)
        {
            if (!_enemyFits.TryGetValue(weaponClass, out EnemyBladeFit fit))
                return;

            if (fit.Count - fit.LastLoggedCount < EnemyFitLogEvery)
                return;

            if (!SolveEnemyBladeFit(fit))
                return;

            fit.LastLoggedCount = fit.Count;

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 262 fit: {0} n={1} o=({2:F3},{3:F3},{4:F3}) u=({5:F3},{6:F3},{7:F3}) "
                + "rms {8:F3} m.", weaponClass, fit.Count, fit.O.x, fit.O.y, fit.O.z,
                fit.U.x, fit.U.y, fit.U.z, fit.Rms));
        }

        /// <summary>The enemy blade radius the fit cannot supply on its own -
        /// the same fallback the classic chord test already used.</summary>
        private static readonly float EnemyBladeRadius =
            VrConfig.Float("combat_block_their_radius", 0.05f);

        private static bool IsShield(WeaponComponentData usage)
        {
            return usage != null && usage.IsShield;
        }

        private static ShieldShape ShapeFor(string key, string label, List<MetaMesh> meshes)
        {
            if (key.Length != 0 && _shields.TryGetValue(key, out ShieldShape cached))
                return cached;

            ShieldShape shape = MeasureShield(label, meshes);

            if (key.Length != 0)
                _shields[key] = shape;

            return shape;
        }

        /// <summary>The item-mesh variant of ShapeFor. The cache is checked
        /// BEFORE the mesh is built: MetaMesh.GetCopy is a native copy, and
        /// building one per block test per frame would leak a mesh a frame.</summary>
        private static ShieldShape ItemShapeFor(string key, string label, ItemObject item)
        {
            if (key.Length != 0 && _shields.TryGetValue(key, out ShieldShape cached))
                return cached;

            ShieldShape shape = MeasureShield(label, ItemMeshes(item));

            if (key.Length != 0)
                _shields[key] = shape;

            return shape;
        }

        /// <summary>
        /// The item's OWN meshes (TEST 242). This is the fix for the NPC item
        /// measurements: the engine's equipment-slot entity on an NPC carries
        /// the batched gear, and the SAME shield measured (1.210,1.282,0.840) m
        /// on one spawn, (1.077,1.419,0.643) on the next and (0.346,1.446,1.181)
        /// on the third. A shield is about half a metre. The item's mesh name is
        /// the mesh the item is authored with, so it measures the item and not
        /// whatever else that agent is wearing.
        /// </summary>
        private static List<MetaMesh> ItemMeshes(ItemObject item)
        {
            var list = new List<MetaMesh>(1);

            if (item == null)
                return list;

            try
            {
                MetaMesh meta = item.WeaponDesign != null
                    ? item.GetCraftedMultiMesh(true)
                    : (!string.IsNullOrEmpty(item.MultiMeshName)
                        ? MetaMesh.GetCopy(item.MultiMeshName, true, true)
                        : null);

                if (meta != null && meta.IsValid)
                    list.Add(meta);
            }
            catch (Exception)
            {
                // A mesh we cannot build is a shape we do not have; the caller
                // falls back to the guard radius or the impact point.
            }

            return list;
        }

        /// <summary>
        /// The guard sphere read out of a shield's AABB: the centre of the box
        /// and half the SMALLER OF ITS TWO LARGEST dimensions.
        ///
        /// It does not try to decide whether the model is a shield - the caller
        /// only ever asks about items whose IsShield is true. That matters,
        /// because the first session measured the player's kite shield as
        /// (0.393, 0.509, 1.012) m: a grip pose can rotate the plate inside its
        /// own entity frame, so no axis is the thickness and a plate test just
        /// refuses a shield that is plainly a shield. Half the middle dimension
        /// stays on the face for any rotation; combat_shield_radius overrides
        /// it when a model disagrees.
        /// </summary>
        private static ShieldShape MeasureShield(string itemId, List<MetaMesh> meshes)
        {
            var shape = new ShieldShape();

            if (meshes == null || !Bounds(meshes, out Vec3 min, out Vec3 max))
                return shape;

            Vec3 size = max - min;
            float big = Math.Max(size.x, Math.Max(size.y, size.z));
            float small = Math.Min(size.x, Math.Min(size.y, size.z));
            float mid = size.x + size.y + size.z - big - small;

            float overridden = VrConfig.Float("combat_shield_radius", 0f);
            shape.Valid = true;
            shape.Centre = (min + max) * 0.5f;
            shape.Radius = overridden > 0f ? overridden : Math.Max(0.05f, mid * 0.5f);

            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "TEST 241 shield: '{0}' measures ({1:F3},{2:F3},{3:F3}) m; the guard sphere "
                + "is {4:F3} m at its centre, in the item's own space.",
                itemId.Length == 0 ? "?" : itemId, size.x, size.y, size.z, shape.Radius));

            return shape;
        }

        public static void Reset()
        {
            for (int i = 0; i < Roles; i++)
            {
                _cache[i].Valid = false;
                _cache[i].ItemId = string.Empty;
                _cache[i].Source = UIntPtr.Zero;
                _cache[i].Stamp = 0;
            }

            _shields.Clear();
            _shapes.Clear();
            _plates.Clear();

            // _enemyFits is deliberately NOT cleared here (TEST 262): it is a
            // fit of a WEAPON CLASS's fixed geometry, not per-mission state, and
            // it takes real NPC-vs-NPC blows to accumulate - clearing it on
            // every mission load would mean refitting from zero every battle for
            // no reason the geometry ever changes.
        }
    }
}
