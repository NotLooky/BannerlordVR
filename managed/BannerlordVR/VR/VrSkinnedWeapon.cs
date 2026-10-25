using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.DotNet;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    /// <summary>
    /// The weapon you SEE in the hand, drawn on an entity of our own.
    ///
    /// RECOVERED 2026-09-23 from the last build that contained it -
    /// managed\BannerlordVR\obj\Release\BannerlordVR.dll, 2026-09-21 03:36,
    /// built after TEST 233 - decompiled with ilspycmd and cleaned back to
    /// source. The behaviour is the shipped behaviour; the log strings are kept
    /// verbatim because the TEST 226-233 journal in config\BannerlordVR.cfg
    /// quotes them. This is the body behind weapon_skinned = 1.
    ///
    /// WHY IT EXISTS (TEST 226-229). The engine rewrites every equipment
    /// entity's frame after our tick and re-adds its metameshes - TEST 229
    /// counted it putting a vanilla mask back 600 times in 600 ticks, so
    /// hiding loses that race every frame. Instead of fighting it, each
    /// wielded item's metameshes are COPIED onto a GameEntity the engine never
    /// attached to the agent (TEST 228), placed from the hand bone after our
    /// IK, and the engine's own meshes are taken OFF its entity while we hold
    /// the item - removal holds, masking does not (TEST 229 metameshes, TEST
    /// 233 cloth simulators). Cloth-simulated items (a lance's pennon) are
    /// copied through their cloth component's metamesh and stay rigid.
    /// Everything is put back on release.
    ///
    /// ALIGNMENT is per weapon, from the config (TEST 231):
    ///   skin_item_&lt;StringId&gt;_x|y|z|yaw|pitch|roll    this exact item
    ///   skin_class_&lt;WeaponClass&gt;_x|y|z|yaw|pitch|roll   its class
    ///   skin_main_* / skin_off_*                     the hand it is in
    /// The hand fallback does not apply to bows or crossbows: they start at
    /// zero, since a sword's yaw means nothing on a bow.
    ///
    /// COST, PLAINLY: visual only. Hits, reach and blocking still use the
    /// vanilla weapon, so what you see and what you hit diverge.
    /// </summary>
    public static class VrSkinnedWeapon
    {
        /// <summary>One item held on one hand: the engine's slot entity it was
        /// copied from, our own entity's meshes, and everything needed to give
        /// the engine's copy back.</summary>
        private sealed class Held
        {
            public UIntPtr Source;
            public EquipmentIndex Slot;

            public readonly List<MetaMesh> Copies = new List<MetaMesh>();
            public int Cloths;

            public string ItemId = string.Empty;
            public string Class = string.Empty;
            public bool Ranged;
            public string PoseKey;

            public MatrixFrame Frame = MatrixFrame.Identity;
            public bool Dirty;

            /// <summary>The entity frame this tick's Drive wrote, so the physical
            /// combat pass can compose the blade without reading the engine
            /// back. See VrBlade.</summary>
            public MatrixFrame World = MatrixFrame.Identity;

            /// <summary>Bumped when this hold is attached or its pose edited, so
            /// VrBlade re-measures the meshes and never keeps a stale segment.
            /// </summary>
            public int BladeStamp;

            /// <summary>The item bone's frame in the wrist's frame, frozen once the
            /// animation has settled. See TEST 230 / TEST 272.</summary>
            public MatrixFrame ItemLocal;
            public bool Frozen;

            /// <summary>TEST 499: the carrying hand bone's world frame the last
            /// Drive placed it from (VrBow's arrow rests on that hand).</summary>
            public MatrixFrame Hand;
            public bool HaveHand;

            /// <summary>TEST 272: settle tracking. StartedAt is when VR took over
            /// the agent (0 until then); Anchor/WindowStart mark the current run of
            /// "the grip has not moved".</summary>
            public long StartedAt;
            public long WindowStart;
            public MatrixFrame Anchor;

            /// <summary>"spawn", "holster" or "wheel/pickup" - only for the log.</summary>
            public string Path = string.Empty;

            /// <summary>TEST 272: when to compare the live grip with the frozen one, once.</summary>
            public long CheckAt;

            /// <summary>The engine's own metameshes, held so the native objects
            /// stay alive while they are off the entity, and put back on
            /// release. See TEST 229.</summary>
            public readonly List<MetaMesh> Originals = new List<MetaMesh>();
            public bool Removed;

            public readonly List<ClothSimulatorComponent> ClothOriginals =
                new List<ClothSimulatorComponent>();
            public bool ClothRemoved;

            /// <summary>TEST 305: missiles stuck in this item (a shield), drawn
            /// on our copy at the engine's own attach frames. Apart from Copies
            /// on purpose: the Dirty loop sets every copy to Frame, and
            /// VrBlade.TryGetShield measures Copies into the plate.</summary>
            public readonly List<MetaMesh> Stuck = new List<MetaMesh>();
            public readonly List<MatrixFrame> StuckLocal = new List<MatrixFrame>();
            public int StuckSeen;

            /// <summary>TEST 305: the engine's own drawing of those missiles -
            /// child entities of its slot entity, at the vanilla spot - taken
            /// off and held like Originals, put back on release.</summary>
            public readonly List<KeyValuePair<UIntPtr, MetaMesh>> StuckOriginals =
                new List<KeyValuePair<UIntPtr, MetaMesh>>();
            public int ChildSeen = -1;
        }

        public static bool Enabled { get; }

        /// <summary>weapon_skinned_hide = remove (default) takes the engine's
        /// metameshes off its entity; mask only sets the visibility mask, which
        /// TEST 229 measured the engine rewriting within a tick.</summary>
        private static readonly bool RemoveVanilla;

        /// <summary>The same choice for the cloth component: weapon_skinned_cloth
        /// = remove (default) or mask. See TEST 233.</summary>
        private static readonly bool RemoveCloth;

        private static bool _clothRemovalFailed;
        private static int _clothReadded;

        private const int Roles = 2;
        private const int MaxMeta = 6;
        private const int MaxMesh = 8;
        private const int MaxReadds = 60;

        /// <summary>weapon_skinned_steady: hold the grip constant against the
        /// wrist instead of following the item (finger) bone - TEST 230.</summary>
        private static readonly bool Steady;

        /// <summary>weapon_skinned_settle: the longest the grip is re-read from the
        /// animation before it is frozen whatever it is doing, in seconds.</summary>
        private static readonly float Settle;

        /// <summary>weapon_skinned_stable: how long the grip must hold still (within
        /// StableDeg / StableM) before it is frozen, in seconds. TEST 272: a fixed
        /// timer froze a different moment of the animation on every path - spawn,
        /// pickup, holster and wheel draws all settle at different times.</summary>
        private static readonly float Stable;

        /// <summary>weapon_skinned_min: the grip is never frozen before this long
        /// after VR took over the agent. TEST 272 log: the finger bone PAUSES
        /// mid-animation (a pickup paused at 0.89 s, 50 degrees from the idle hold a
        /// wheel draw reaches), so "held still" is not "reached idle".</summary>
        private static readonly float MinDwell;

        /// <summary>TEST 273: a FIXED grip for the main hand's melee weapons
        /// (weapon_fixed_grip_main, 12 numbers s,f,u,origin; weapon_fixed_grip = 0
        /// goes back to measuring). The finger bone's pose against the wrist is
        /// bimodal - A (forward -0.13,-0.97,-0.19) for 1.5 s or more after a spawn or
        /// draw, then B (-0.40,-0.90,0.16) when the hand's grip pose arrives - so no
        /// freeze time is right. B is the idle hold the per-weapon poses were tuned
        /// on (wheel draws at 0.6 s were in B).</summary>
        private static readonly MatrixFrame? FixedMain;
        private static bool _loggedFixed;
        private const float StableCos = 0.99966f;   // cos(1.5 deg)
        private const float StableM = 0.004f;

        /// <summary>The mission each hand last attached a weapon in; the first
        /// attach of a mission is the spawn-held weapon. Log label only.</summary>
        private static readonly Mission[] _roleMission = new Mission[Roles];

        /// <summary>Set by VrHolster just before it draws. Log label only.</summary>
        internal static long HolsterDrawAt;

        // TEST 331b: a newly held weapon's visual eases in from where it was
        // taken (the ground, a holster, mid-fall) instead of appearing in the
        // fist. Per role; pending until the next Drive picks it up.
        private static readonly float GlideMps = Math.Max(0.1f, VrConfig.Float("grab_glide_mps", 2.5f));
        private static readonly float GlideMin = VrConfig.Float("grab_glide_min_s", 0.12f);
        private static readonly float GlideMax = VrConfig.Float("grab_glide_max_s", 0.30f);
        private static readonly MatrixFrame[] _glideFrom = new MatrixFrame[2];
        private static readonly long[] _glidePending = new long[2];
        private static readonly long[] _glideStart = new long[2];
        private static readonly float[] _glideDur = new float[2];

        private static readonly Held[] _held;
        private static readonly GameEntity[] _entity;

        /// <summary>The mission each entity was made in. NOT the scene pointer
        /// (TEST 308): the training arena and every tournament match load the
        /// same scene again, a new scene can sit at the old one's address, and
        /// the weapon was then drawn on an entity of a scene that no longer
        /// renders - invisible weapons, only in arenas. We hold the Mission, so
        /// it cannot be reused.</summary>
        private static readonly Mission[] _mission;

        private static bool _broken;
        private static bool _removalFailed;
        private static bool _loggedFirst;

        /// <summary>Monotonic; every attach and every pose edit takes the next
        /// value, so VrBlade can tell "the same weapon, unchanged" from
        /// "re-measure".</summary>
        private static int _bladeStamp;

        private static int _reshown;
        private static int _readded;
        private static int _ticks;

        private const int Dof = 6;
        private static readonly string[] DofName;
        private static readonly string[] RoleName;
        private static readonly Dictionary<string, float[]> _poses;

        // --- live tuning (skin_tune = 1), TEST 229/231 -------------------------
        private static readonly bool Tune;
        private static readonly string KeyTool;
        private static readonly string KeyHand;
        private static readonly string KeyScope;
        private static readonly string KeyZero;
        private static readonly string KeySave;
        private static readonly string KeyFine;
        private static readonly string KeyGrip;
        private static readonly string KeyEnable;
        private static readonly string KeyXPlus;
        private static readonly string KeyXMinus;
        private static readonly string KeyYPlus;
        private static readonly string KeyYMinus;
        private static readonly string KeyZPlus;
        private static readonly string KeyZMinus;

        private static int _tuneRole;
        private static bool _tuneRotate;
        // TEST 552: ITEM by default. Saving to the class re-aligned every other
        // weapon of that class (two two-handed swords, two LargeShields, 2026-10-13);
        // the class stays one key away for seeding untuned items.
        private static bool _tuneItemScope = true;
        private static bool _tuneGrip;
        private static bool _tuneOn;
        private static long _tuneStamp;
        private static double _tuneLogAt;

        static VrSkinnedWeapon()
        {
            Enabled = VrConfig.Bool("weapon_skinned", false);
            RemoveVanilla = !string.Equals(
                VrConfig.String("weapon_skinned_hide", "remove"), "mask",
                StringComparison.OrdinalIgnoreCase);
            RemoveCloth = !string.Equals(
                VrConfig.String("weapon_skinned_cloth", "remove"), "mask",
                StringComparison.OrdinalIgnoreCase);
            Steady = VrConfig.Bool("weapon_skinned_steady", true);
            Settle = VrConfig.Float("weapon_skinned_settle", 4.0f);
            Stable = VrConfig.Float("weapon_skinned_stable", 0.3f);
            MinDwell = VrConfig.Float("weapon_skinned_min", 1.5f);
            FixedMain = VrConfig.Bool("weapon_fixed_grip", true) ? ParseGrip(VrConfig.String(
                "weapon_fixed_grip_main",
                "-0.91,-0.05,0.40,-0.40,-0.90,0.16,0.47,-0.05,0.88,0.084,0.015,-0.008")) : null;

            _held = new Held[Roles];
            _entity = new GameEntity[Roles];
            _mission = new Mission[Roles];

            DofName = new[] { "x", "y", "z", "yaw", "pitch", "roll" };
            RoleName = new[] { "main", "off" };
            _poses = new Dictionary<string, float[]>(StringComparer.Ordinal);

            Tune = VrConfig.Bool("skin_tune", false);
            KeyTool = VrConfig.String("skin_key_tool", "Numpad0");
            KeyHand = VrConfig.String("skin_key_hand", "NumpadMultiply");
            KeyScope = VrConfig.String("skin_key_scope", "NumpadSlash");
            KeyZero = VrConfig.String("skin_key_zero", "Numpad5");
            KeySave = VrConfig.String("skin_key_save", "NumpadEnter");
            KeyFine = VrConfig.String("skin_key_fine", "RightShift");
            KeyGrip = VrConfig.String("skin_key_grip", "NumpadMinus");
            KeyEnable = VrConfig.String("skin_key_enable", "NumpadPlus");
            KeyXPlus = VrConfig.String("skin_key_x_plus", "Right");
            KeyXMinus = VrConfig.String("skin_key_x_minus", "Left");
            KeyYPlus = VrConfig.String("skin_key_y_plus", "Up");
            KeyYMinus = VrConfig.String("skin_key_y_minus", "Down");
            KeyZPlus = VrConfig.String("skin_key_z_plus", "PageUp");
            KeyZMinus = VrConfig.String("skin_key_z_minus", "PageDown");

            for (int i = 0; i < Roles; i++)
                _poses["role:" + RoleName[i]] = LoadPose("role:" + RoleName[i]) ?? new float[6];
        }

        /// <summary>"item:foo_id" becomes skin_item_foo_id_, "role:main" becomes
        /// skin_main_. Anything outside [A-Za-z0-9_.-] in the name becomes '_'.</summary>
        private static string Prefix(string key)
        {
            int colon = key.IndexOf(':');
            string scope = key.Substring(0, colon);
            string rest = key.Substring(colon + 1);

            if (scope == "role")
                return "skin_" + rest + "_";

            var name = new StringBuilder(rest.Length);
            foreach (char c in rest)
                name.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');

            return "skin_" + scope + "_" + name + "_";
        }

        // TEST 751: pose profiles. 0 is Original, the plain keys; 1 and 2 save under
        // skin_p1_ / skin_p2_ and read their own keys first, falling back to
        // Original for any pose they never saved - so a profile starts as a copy
        // and Original is never touched from one. Only the six pose numbers are
        // profiled; the settled grips (GripKey) are measurements and stay shared.
        private static int _profile = Math.Max(0, Math.Min(2, VrConfig.Int("skin_profile", 0)));

        internal static int Profile => _profile;

        private static string ProfileName => _profile == 0 ? "Original" : "Profile " + _profile;

        private static string PosePrefix(string key, int profile)
        {
            string prefix = Prefix(key);
            return profile == 0 ? prefix : "skin_p" + profile + "_" + prefix.Substring("skin_".Length);
        }

        internal static void SetProfile(int profile)
        {
            profile = Math.Max(0, Math.Min(2, profile));
            if (profile == _profile)
                return;

            _profile = profile;
            VrConfig.Save("skin_profile", profile.ToString(CultureInfo.InvariantCulture));
            VrHandCalibration.SetProfile(profile);

            // Unsaved working copies belong to the profile they were made in.
            _poses.Clear();
            _toolDirty.Clear();
            for (int i = 0; i < Roles; i++)
                _poses["role:" + RoleName[i]] = LoadPose("role:" + RoleName[i]) ?? new float[6];
            Refresh();
            VrLog.Info("TEST 751: weapon pose profile " + (profile == 0 ? "Original" : profile.ToString()) + ", saved.");
        }

        /// <summary>Reads a six-number pose; null when the config has none of the
        /// six keys, so "not aligned" and "aligned to zero" stay different.
        /// TEST 751: the profile's own keys, then Original's.</summary>
        private static float[] LoadPose(string key)
        {
            return LoadPoseAt(PosePrefix(key, _profile))
                   ?? (_profile != 0 ? LoadPoseAt(Prefix(key)) : null);
        }

        private static float[] LoadPoseAt(string prefix)
        {
            float[] pose = new float[6];
            bool any = false;

            for (int i = 0; i < Dof; i++)
            {
                float value = VrConfig.Float(prefix + DofName[i], float.NaN);
                if (!float.IsNaN(value))
                {
                    pose[i] = value;
                    any = true;
                }
            }

            return any ? pose : null;
        }

        private static float[] PoseOf(string key)
        {
            if (key == null)
                return null;

            float[] pose;
            if (_poses.TryGetValue(key, out pose))
                return pose;

            pose = LoadPose(key);
            if (pose != null)
                _poses[key] = pose;

            return pose;
        }

        /// <summary>The pose this item is aligned by, most specific first:
        /// item, then class, then the hand - except bows and crossbows, which
        /// never take the hand fallback. See TEST 231. TEST 495: with five-finger
        /// hands on, the poses aligned for them (fitem:, fclass:) come first.</summary>
        private static string ResolvePoseKey(Held h, int role)
        {
            if (VrHandMesh.Enabled)
            {
                string fitem = h.ItemId.Length == 0 ? null : "fitem:" + h.ItemId;
                if (PoseOf(fitem) != null)
                    return fitem;

                string fclass = h.Class.Length == 0 ? null : "fclass:" + h.Class;
                if (PoseOf(fclass) != null)
                    return fclass;
            }

            string item = h.ItemId.Length == 0 ? null : "item:" + h.ItemId;
            if (PoseOf(item) != null)
                return item;

            string weaponClass = h.Class.Length == 0 ? null : "class:" + h.Class;
            if (PoseOf(weaponClass) != null)
                return weaponClass;

            if (!h.Ranged)
                return "role:" + RoleName[role];

            return null;
        }

        /// <summary>Six numbers - x y z in metres, yaw pitch roll in degrees -
        /// into a frame in the carrying bone's space, with the weapon_off_* /
        /// weapon_yaw/pitch/roll_deg nudge applied on top, as TEST 227 had it.</summary>
        /// <summary>The tuned pose; TEST 489: then slid along the item's own haft
        /// (its z, the blade end positive) by weapon_haft_shift_m_[WeaponClass] -
        /// the hand closing on a one-handed sword's grip sat in its guard.</summary>
        private static MatrixFrame BuildFrame(float[] pose, string weaponClass)
        {
            MatrixFrame frame = MatrixFrame.Identity;
            const float ToRad = (float)(Math.PI / 180.0);

            frame.rotation.RotateAboutUp(pose[3] * ToRad);
            frame.rotation.RotateAboutSide(pose[4] * ToRad);
            frame.rotation.RotateAboutForward(pose[5] * ToRad);
            frame.origin = new Vec3(pose[0], pose[1], pose[2], -1f)
                           + frame.rotation.u * VrConfig.Float("weapon_haft_shift_m_" + weaponClass, 0f);

            if (!VrWeaponEntity.HaveNudge)
                return frame;

            return VrWeaponEntity.NudgeFrame.TransformToParent(frame);
        }

        /// <summary>
        /// The per-tick drive for one hand: attach on change, hide the engine's
        /// copy, place our entity from the hand bone. Returns false whenever the
        /// entity drive should run instead (off, broken, nothing held, nothing
        /// copied), so the two mechanisms never both write.
        /// </summary>
        public static bool Drive(Agent agent, VrHandSlot slot, MatrixFrame handWorld,
                                 MatrixFrame itemWorld)
        {
            if (!Enabled || _broken)
                return false;

            int role = (int)slot.Role;

            try
            {
                if (!slot.Valid || slot.Equipment == EquipmentIndex.None)
                {
                    Detach(agent, role, restore: false);
                    return false;
                }

                MBAgentVisuals visuals = agent.AgentVisuals;
                GameEntity entity = visuals != null ? visuals.GetEntity() : null;
                if (entity == null)
                    return false;

                WeakGameEntity source = agent.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                if (!source.IsValid)
                {
                    Detach(agent, role, restore: false);
                    return false;
                }

                // TEST 308: a hold from the last mission died with its scene - drop
                // it untouched, or an engine entity that reuses its address would
                // be taken for the same item and never attached here.
                Held held = _held[role];
                if (held != null && _mission[role] != Mission.Current)
                {
                    _held[role] = null;
                    held = null;
                }

                if (held == null || held.Source != source.Pointer)
                {
                    Detach(agent, role, restore: false);
                    Attach(agent, entity, source, slot, role);
                    held = _held[role];
                }

                if (held.Copies.Count == 0)
                    return false;

                HideSource(source, held);
                Stuck(agent, source, held, role);

                if (held.Dirty)
                {
                    held.Dirty = false;
                    held.BladeStamp = ++_bladeStamp;

                    foreach (MetaMesh copy in held.Copies)
                        copy.Frame = held.Frame;

                    for (int s = 0; s < held.Stuck.Count; s++)
                        held.Stuck[s].Frame = held.Frame.TransformToParent(held.StuckLocal[s]);
                }

                // TEST 230: steady - the wrist frame times a grip offset frozen
                // after weapon_skinned_settle seconds, so finger animation no
                // longer swings the weapon. Otherwise the item bone's frame is
                // used as-is. (The old VrMelee.EngineOwnsBody clause is gone with
                // the melee subsystem; it was false unless melee drove the body.)
                MatrixFrame target = Steady ? Place(held, slot, handWorld, itemWorld) : itemWorld;

                // TEST 304g: a main hand slid along a two-hander's shaft.
                target.origin += VrTwoHand.SlideOffset(slot.Role, handWorld);

                // TEST 491: seated in the palm the way a real grip holds it (VrHandMesh).
                target.origin += handWorld.rotation.TransformToParent(VrHandMesh.SeatOf(slot.Role));
                target = ApplyGlide(role, held, target);
                held.World = target;
                held.Hand = handWorld;
                held.HaveHand = true;
                _entity[role].SetGlobalFrame(in target, isTeleportation: true);
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                VrLog.Error("TEST 229: own-entity weapon failed; weapons go back to the "
                            + "entity drive. Nothing else is affected.", ex);
                return false;
            }
        }

        /// <summary>
        /// TEST 331b: start a glide for this hand. `visual` is where the weapon is
        /// SEEN now (an entity frame times its metamesh frame), so the glide runs
        /// mesh to mesh whatever pose offset this hand's copy carries.
        /// </summary>
        internal static void Glide(HandRole role, MatrixFrame visual)
        {
            int i = (int)role;
            visual.rotation.Orthonormalize();
            _glideFrom[i] = visual;
            _glidePending[i] = Stopwatch.GetTimestamp();
            _glideStart[i] = 0;
        }

        /// <summary>True while this hand's weapon is gliding in, or about to:
        /// its motion is not a swing, and nothing should collide or cut with it.
        /// </summary>
        internal static bool Gliding(HandRole role)
        {
            int i = (int)role;
            return _glidePending[i] != 0 || _glideStart[i] != 0;
        }

        private static MatrixFrame ApplyGlide(int i, Held held, MatrixFrame target)
        {
            long now = Stopwatch.GetTimestamp();

            if (_glidePending[i] != 0)
            {
                // Never picked up within half a second (the hold failed): drop it.
                if (now - _glidePending[i] > Stopwatch.Frequency / 2)
                {
                    _glidePending[i] = 0;
                    return target;
                }

                _glidePending[i] = 0;
                _glideStart[i] = now;

                // The entity frame that would show the mesh where it is seen.
                _glideFrom[i] = _glideFrom[i].TransformToParent(held.Frame.Inverse());

                float d = (target.origin - _glideFrom[i].origin).Length;
                _glideDur[i] = Math.Max(GlideMin, Math.Min(GlideMax, d / GlideMps));
                VrLog.Info("TEST 331b glide: '" + held.ItemId + "' flies " + d.ToString("F2")
                           + " m into the hand over " + (_glideDur[i] * 1000f).ToString("F0") + " ms.");
            }

            if (_glideStart[i] == 0)
                return target;

            float t = (float)((now - _glideStart[i]) / (double)Stopwatch.Frequency) / _glideDur[i];
            if (t >= 1f)
            {
                _glideStart[i] = 0;
                return target;
            }

            return GlideAt(_glideFrom[i], target, t);
        }

        /// <summary>The glide's frame at t in [0, 1]: smoothstep along the
        /// straight line, shortest-arc turn. Pure; the math tests call it.</summary>
        internal static MatrixFrame GlideAt(MatrixFrame from, MatrixFrame to, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            float s = t * t * (3f - 2f * t);
            MatrixFrame f = to;
            f.origin = from.origin + (to.origin - from.origin) * s;
            f.rotation = VrWeight.Blend(from.rotation, to.rotation, s);
            return f;
        }

        /// <summary>
        /// TEST 230/272: the weapon placed against the WRIST - handWorld, which we
        /// write every tick - times the item bone's offset in that wrist's frame.
        /// The offset follows the animation until it has held still for
        /// weapon_skinned_stable seconds, then is frozen. Freezing on a timer
        /// (0.6 s after attach) caught a different moment of the animation on each
        /// path - spawn mid deployment-to-idle, pickup and draws mid wield - so the
        /// same per-weapon pose looked rotated unless it came from the wheel.
        /// The clock does not start while VR does not yet own the agent
        /// (deployment), so a still deployment pose is never the one frozen.
        /// </summary>
        private static MatrixFrame Place(Held h, VrHandSlot slot, MatrixFrame handWorld,
                                         MatrixFrame itemWorld)
        {
            if (!h.Frozen && FixedMain.HasValue && slot.Role == HandRole.Main && !h.Ranged)
            {
                h.ItemLocal = FixedMain.Value;
                h.Frozen = true;
                if (!_loggedFixed)
                {
                    _loggedFixed = true;
                    VrLog.Info("TEST 273: the main hand's melee weapons sit on a FIXED grip "
                               + "(weapon_fixed_grip_main), not on the animation's finger bone, which "
                               + "flips between two poses. weapon_fixed_grip = 0 goes back.");
                }
            }

            if (!h.Frozen)
            {
                long now = Stopwatch.GetTimestamp();
                MatrixFrame live = handWorld.TransformToLocalNonOrthogonal(itemWorld);
                h.ItemLocal = live;

                if (!VrMissionPhase.VrDrivesLook)
                {
                    h.StartedAt = 0L;
                }
                else if (h.StartedAt == 0L)
                {
                    h.StartedAt = now;
                    h.Anchor = live;
                    h.WindowStart = now;
                }
                else if (!Near(h.Anchor, live))
                {
                    h.Anchor = live;
                    h.WindowStart = now;
                }

                if (h.StartedAt != 0L)
                {
                    float still = (float)(now - h.WindowStart) / Stopwatch.Frequency;
                    float total = (float)(now - h.StartedAt) / Stopwatch.Frequency;
                    bool settled = still >= Stable && total >= MinDwell;

                    if (settled || total >= Settle)
                    {
                        h.Frozen = true;
                        if (settled)
                            RememberGrip(slot.Role, h.ItemId, live);
                        h.CheckAt = now + 3 * Stopwatch.Frequency;
                        Vec3 f = live.rotation.f.NormalizedCopy();
                        Vec3 u = live.rotation.u.NormalizedCopy();
                        VrLog.Info("TEST 272 " + slot.Role + " '" + h.ItemId + "' (" + h.Path
                                   + "): grip " + (settled ? "settled" : "NOT settled, frozen at the cap")
                                   + " after " + total.ToString("0.00", CultureInfo.InvariantCulture)
                                   + " s, forward (" + V(f) + ") up (" + V(u) + "), at ("
                                   + V(live.origin) + ") m. Held constant against the wrist; "
                                   + "weapon_skinned_steady = 0 goes back.");
                    }
                }
            }

            else if (h.CheckAt != 0L && Stopwatch.GetTimestamp() >= h.CheckAt)
            {
                // One look, 3 s after the freeze: how far has the live grip moved from
                // the frozen one? Large while idle = frozen on the wrong moment.
                h.CheckAt = 0L;
                MatrixFrame live = handWorld.TransformToLocalNonOrthogonal(itemWorld);
                float cos = Vec3.DotProduct(live.rotation.f.NormalizedCopy(), h.ItemLocal.rotation.f.NormalizedCopy());
                float deg = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, cos))) * 180.0 / Math.PI);
                VrLog.Info("TEST 272 " + slot.Role + " '" + h.ItemId + "' (" + h.Path + "): 3 s after the freeze the live grip "
                           + "is " + deg.ToString("0", CultureInfo.InvariantCulture) + " deg from the frozen one, forward ("
                           + V(live.rotation.f.NormalizedCopy()) + "). Near 0 while idle = frozen on the idle hold.");
            }

            return handWorld.TransformToParent(h.ItemLocal);
        }

        /// <summary>Twelve numbers - s, f, u, origin - into a frame, forward and up
        /// made exactly perpendicular; null when the text is not twelve numbers.</summary>
        private static MatrixFrame? ParseGrip(string text)
        {
            string[] parts = text.Split(',');
            if (parts.Length != 12)
                return null;

            float[] v = new float[12];
            for (int i = 0; i < 12; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return null;

            Vec3 f = new Vec3(v[3], v[4], v[5]).NormalizedCopy();
            Vec3 u = new Vec3(v[6], v[7], v[8]);
            u = (u - f * Vec3.DotProduct(u, f)).NormalizedCopy();
            Vec3 side = Vec3.CrossProduct(f, u);
            return new MatrixFrame(new Mat3(side, f, u), new Vec3(v[9], v[10], v[11]));
        }

        /// <summary>Two grips within about 1.5 degrees and 4 mm of each other.</summary>
        private static bool Near(MatrixFrame a, MatrixFrame b)
        {
            return Vec3.DotProduct(a.rotation.f.NormalizedCopy(), b.rotation.f.NormalizedCopy()) >= StableCos
                   && Vec3.DotProduct(a.rotation.u.NormalizedCopy(), b.rotation.u.NormalizedCopy()) >= StableCos
                   && (a.origin - b.origin).Length <= StableM;
        }

        private static string V(Vec3 v)
        {
            return v.x.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                   + v.y.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                   + v.z.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Copies the item's metameshes (and any cloth simulator's metamesh,
        /// TEST 232) onto our own entity, then takes the engine's copy off its
        /// entity while we hold the item.
        /// </summary>
        private static void Attach(Agent agent, GameEntity agentEntity, WeakGameEntity source,
                                   VrHandSlot slot, int i)
        {
            var held = new Held
            {
                Source = source.Pointer,
                Slot = slot.Equipment,
                BladeStamp = ++_bladeStamp,
            };
            _held[i] = held;

            Identify(agent, held);

            // Log label only (TEST 272). ponytail: "first attach of the mission" stands
            // for "spawned holding it"; a player spawning empty-handed reads as spawn.
            Mission mission = Mission.Current;
            bool first = !ReferenceEquals(_roleMission[i], mission);
            _roleMission[i] = mission;
            held.Path = Stopwatch.GetTimestamp() - HolsterDrawAt < Stopwatch.Frequency / 2 ? "holster"
                        : first ? "spawn" : "wheel/pickup";

            held.PoseKey = ResolvePoseKey(held, i);
            held.Frame = BuildFrame(PoseOf(held.PoseKey) ?? new float[6], held.Class);

            // TEST 498: a grip this item settled to before is taken at once - the
            // settle waits 1.5 s on the wield animation, and the fingers could not
            // close on a weapon still moving in the hand (the bow, every grab).
            // The main hand's melee weapons have their fixed grip instead.
            if (Steady && !(FixedMain.HasValue && i == (int)HandRole.Main && !held.Ranged)
                && TryRememberedGrip((HandRole)i, held.ItemId, out MatrixFrame settledGrip))
            {
                held.ItemLocal = settledGrip;
                held.Frozen = true;
                VrLog.Info("TEST 498 " + slot.Role + " '" + held.ItemId + "': its settled grip is taken at once ("
                           + GripKey((HandRole)i, held.ItemId) + ").");
            }

            VrLog.Info("TEST 231 " + slot.Role + ": '" + held.ItemId + "' (class " + held.Class
                       + (held.Ranged ? ", ranged" : "") + ") is aligned by "
                       + (held.PoseKey ?? "a zero pose - nothing saved for it") + ".");

            Scene scene = agentEntity.Scene;
            if (scene == null)
                return;

            if (_entity[i] == null || _mission[i] != mission)
            {
                _entity[i] = GameEntity.CreateEmptyDynamic(scene, false);
                _mission[i] = mission;
                VrLog.Info("TEST 308 " + slot.Role + ": a new entity for this mission's weapons.");
            }

            GameEntity ours = _entity[i];
            int meshes = source.MultiMeshComponentCount;
            int cloths = source.ClothSimulatorComponentCount;

            VrLog.Info("TEST 232 " + slot.Role + ": the engine's entity for slot " + slot.Equipment
                       + " has " + meshes + " metamesh(es), " + cloths + " cloth simulator(s) and "
                       + source.ChildCount + " child entit(ies).");

            var seen = new HashSet<UIntPtr>();
            var looks = new List<MetaMesh>();
            for (int m = 0; m < meshes && m < MaxMeta; m++)
            {
                MetaMesh meta = source.GetMetaMesh(m);
                if (meta != null && seen.Add(meta.Pointer))
                    looks.Add(meta);
            }

            // TEST 334: several looks of one item (a javelin and its bundle, an
            // arrow and its quiver): in the hand it is the single weapon, the
            // SMALLEST - the holster look contains it. All of them still come
            // off the engine's entity below.
            MetaMesh only = looks.Count > 1 ? VrHolsterView.Smallest(looks) : null;
            if (only != null)
                VrLog.Info("TEST 334 " + slot.Role + ": " + looks.Count + " looks on slot " + slot.Equipment
                           + " (" + VrHolsterView.Sizes(looks) + "); drawing the smallest in the hand.");

            foreach (MetaMesh meta in looks)
            {
                if (only == null || meta == only)
                    AddCopy(meta, ours, held);
            }

            for (int c = 0; c < cloths && c < MaxMeta; c++)
            {
                ClothSimulatorComponent cloth = source.GetClothSimulator(c);
                MetaMesh meta = cloth != null ? cloth.GetFirstMetaMesh() : null;
                if (meta != null && seen.Add(meta.Pointer) && AddCopy(meta, ours, held))
                    held.Cloths = Math.Min(cloths, MaxMeta);
            }

            if (held.Copies.Count == 0)
            {
                VrLog.Warn("TEST 229 " + slot.Role + ": slot " + slot.Equipment + " has no "
                           + "metamesh or cloth mesh to copy; leaving it to the entity drive.");
                return;
            }

            ours.SetVisibilityExcludeParents(true);
            VrLog.Info("TEST 229 " + slot.Role + ": " + held.Copies.Count + " metamesh(es) from "
                       + "slot " + slot.Equipment + " now drawn on our own entity.");

            if (RemoveVanilla && !_removalFailed && meshes > 0)
                TakeOff(source, held, slot);

            if (held.Cloths > 0 && RemoveCloth && !_clothRemovalFailed)
                TakeOffCloth(source, held, slot);
            else if (held.Cloths > 0)
                VrLog.Info("TEST 232 " + slot.Role + ": " + held.Cloths + " cloth simulator(s) on "
                           + "the engine's entity are hidden by mask, not removed. The cloth will "
                           + "not flap, and the engine's own copy may flicker.");

            if (!_loggedFirst)
            {
                _loggedFirst = true;
                VrLog.Info("TEST 229: weapons are drawn on entities of our own, placed from the "
                           + "item bone each tick. This is visual only - hits and reach still use "
                           + "the vanilla weapon.");
            }
        }

        /// <summary>A copy of one metamesh. Sheathed meshes ride with mask 0, so a
        /// copy whose meshes are all masked is turned back on (TEST 225b: a
        /// sheathed sword's masks are 0 and a wielded one's DefaultDynamic).</summary>
        private static bool AddCopy(MetaMesh meta, GameEntity ours, Held h)
        {
            MetaMesh copy = meta.CreateCopy();
            if (copy == null)
                return false;

            copy.Frame = h.Frame;

            int meshes = copy.MeshCount;
            for (int i = 0; i < meshes && i < MaxMesh; i++)
            {
                Mesh mesh = copy.GetMeshAtIndex(i);
                if (mesh != null && mesh.VisibilityMask == 0)
                    mesh.SetVisibilityMask((VisibilityMaskFlags)33);
            }

            Opaque(copy);
            ours.AddMultiMesh(copy, true);
            h.Copies.Add(copy);
            return true;
        }

        /// <summary>TEST 760: Agent.FadeIn (arena practice spawns) ramps the alpha
        /// byte of the agent's mesh colours. A copy made mid-fade keeps that alpha
        /// forever, and the engine only finishes the fade on its own meshes, so the
        /// copy stays a dithered ghost. Copies are always drawn opaque.</summary>
        internal static void Opaque(MetaMesh meta)
        {
            int n = meta?.MeshCount ?? 0;
            for (int i = 0; i < n && i < MaxMesh; i++)
            {
                Mesh mesh = meta.GetMeshAtIndex(i);
                if (mesh != null)
                    mesh.Color |= 0xFF000000u;
            }
        }

        private static void TakeOffCloth(WeakGameEntity source, Held h, VrHandSlot slot)
        {
            int cloths = source.ClothSimulatorComponentCount;
            for (int i = 0; i < cloths && i < MaxMeta; i++)
            {
                ClothSimulatorComponent cloth = source.GetClothSimulator(i);
                if (cloth != null)
                    h.ClothOriginals.Add(cloth);
            }

            VrLog.Info("TEST 233 " + slot.Role + ": taking " + h.ClothOriginals.Count + " cloth "
                       + "simulator(s) of slot " + slot.Equipment + " off the engine's entity. "
                       + "They are held here and put back when the item is released.");

            GameEntity entity = GameEntity.CreateFromWeakEntity(source);
            if (entity == null)
                return;

            foreach (ClothSimulatorComponent cloth in h.ClothOriginals)
                entity.RemoveComponent(cloth);

            source.SetVisibilityExcludeParents(false);
            h.ClothRemoved = h.ClothOriginals.Count > 0;

            VrLog.Info("TEST 233 " + slot.Role + ": the engine's entity now has "
                       + source.ClothSimulatorComponentCount + " cloth simulator(s).");
        }

        private static void MaskCloth(WeakGameEntity source, int cloths)
        {
            if (source.GetVisibilityExcludeParents())
                source.SetVisibilityExcludeParents(false);

            int count = source.ClothSimulatorComponentCount;
            for (int i = 0; i < count && i < cloths && i < MaxMeta; i++)
            {
                ClothSimulatorComponent cloth = source.GetClothSimulator(i);
                if (cloth != null)
                {
                    MetaMesh meta = cloth.GetFirstMetaMesh();
                    if (meta != null)
                        meta.SetVisibilityMask(0);
                }
            }
        }

        private static void TakeOff(WeakGameEntity source, Held h, VrHandSlot slot)
        {
            int meshes = source.MultiMeshComponentCount;
            for (int i = 0; i < meshes && i < MaxMeta; i++)
            {
                MetaMesh meta = source.GetMetaMesh(i);
                if (meta != null)
                    h.Originals.Add(meta);
            }

            VrLog.Info("TEST 229 " + slot.Role + ": taking " + h.Originals.Count + " vanilla "
                       + "metamesh(es) of slot " + slot.Equipment + " off the engine's entity. "
                       + "They are held here and put back when the item is released.");

            foreach (MetaMesh original in h.Originals)
                source.RemoveMultiMesh(original);

            source.SetVisibilityExcludeParents(false);
            h.Removed = h.Originals.Count > 0;
            VrLog.Info("TEST 229 " + slot.Role + ": the engine's entity now has "
                       + source.MultiMeshComponentCount + " metamesh(es).");
        }

        /// <summary>
        /// Every tick an item is held: the engine may have put its meshes back
        /// (TEST 229: it rewrites the mask every frame), so they are taken off
        /// again. Removal gives up after MaxReadds and falls back to masking.
        /// </summary>
        private static void HideSource(WeakGameEntity source, Held h)
        {
            if (h.ClothRemoved)
            {
                int cloths = source.ClothSimulatorComponentCount;
                if (cloths > 0)
                {
                    _clothReadded += cloths;
                    GameEntity entity = GameEntity.CreateFromWeakEntity(source);

                    for (int i = 0; i < cloths && i < MaxMeta; i++)
                    {
                        if (entity == null)
                            break;

                        ClothSimulatorComponent cloth = source.GetClothSimulator(i);
                        if (cloth != null)
                        {
                            entity.RemoveComponent(cloth);
                            h.ClothOriginals.Add(cloth);
                        }
                    }

                    if (_clothReadded > MaxReadds)
                    {
                        _clothRemovalFailed = true;
                        h.ClothRemoved = false;
                        VrLog.Warn("TEST 233: the engine keeps putting its cloth simulator back on "
                                   + "the entity (" + _clothReadded + " times). Removal is given "
                                   + "up; hiding by mask instead. weapon_skinned_cloth = mask does "
                                   + "this from the start.");
                    }
                }
            }
            else if (h.Cloths > 0)
            {
                MaskCloth(source, h.Cloths);
            }

            if (h.Removed)
            {
                int meshes = source.MultiMeshComponentCount;
                if (meshes > 0)
                {
                    _readded += meshes;

                    for (int i = 0; i < meshes && i < MaxMeta; i++)
                    {
                        MetaMesh meta = source.GetMetaMesh(i);
                        if (meta != null)
                        {
                            source.RemoveMultiMesh(meta);
                            h.Originals.Add(meta);
                        }
                    }

                    if (_readded > MaxReadds)
                    {
                        _removalFailed = true;
                        h.Removed = false;
                        VrLog.Warn("TEST 229: the engine keeps putting its metameshes back on the "
                                   + "entity (" + _readded + " times). Removal is given up; hiding "
                                   + "by mask instead. weapon_skinned_hide = mask does this from "
                                   + "the start.");
                    }
                }

                if (h.Removed)
                {
                    Report();
                    return;
                }
            }

            if (source.GetVisibilityExcludeParents())
                source.SetVisibilityExcludeParents(false);

            int count = source.MultiMeshComponentCount;
            for (int i = 0; i < count && i < MaxMeta; i++)
            {
                MetaMesh meta = source.GetMetaMesh(i);
                if (meta == null)
                    continue;

                if (meta.MeshCount > 0)
                {
                    Mesh first = meta.GetMeshAtIndex(0);
                    if (first != null && first.VisibilityMask != 0)
                        _reshown++;
                }

                meta.SetVisibilityMask(0);
            }

            Report();
        }

        private static void Report()
        {
            if (++_ticks < 600)
                return;

            VrLog.Info("TEST 229: in the last " + _ticks + " hidden-item ticks the engine put a "
                       + "vanilla mask back " + _reshown + " time(s) and a removed metamesh back "
                       + _readded + " time(s) (removal " + (_removalFailed ? "given up" : "holding")
                       + "), and a removed cloth simulator back " + _clothReadded + " time(s) "
                       + "(removal " + (_clothRemovalFailed ? "given up"
                           : (RemoveCloth ? "holding" : "off")) + "). All near zero means the "
                       + "vanilla copy stays out; the screen decides.");

            _ticks = 0;
            _reshown = 0;
        }

        /// <summary>
        /// Drops one hand's copies and puts the engine's own meshes back the way
        /// the item is now: mask 0 while it stays sheathed, DefaultDynamic while
        /// it is still wielded. restore=true is Release - it puts the item back
        /// even if the engine had not re-added anything.
        /// </summary>
        /// <summary>Missiles one shield can show; past this the engine's list
        /// keeps counting and the copy stops adding.</summary>
        private const int MaxStuck = 24;

        /// <summary>
        /// TEST 305: arrows and javelins stuck in the held item, on our copy.
        /// The engine records each one on the item itself (MissionWeapon's
        /// attached list, frames local to the item - Agent.AttachWeaponToWeapon)
        /// and draws it as a child of ITS slot entity, which sits at the vanilla
        /// animation spot with its own meshes taken off - so before this the
        /// arrows either floated by the hip or were not there. Each new entry is
        /// drawn on our entity at the copy's mesh frame times the engine's attach
        /// frame, and the engine's own child copy is taken off the way TEST 269g
        /// takes the loaded arrow off the bow. The list survives sheathe, drop
        /// and pickup, so a shield picked up with arrows in it shows them too.
        /// </summary>
        private static void Stuck(Agent agent, WeakGameEntity source, Held h, int role)
        {
            MissionWeapon weapon = agent.Equipment[h.Slot];
            int count = weapon.IsEmpty ? 0 : weapon.GetAttachedWeaponsCount();
            int children = source.ChildCount;

            if (count == h.StuckSeen && children == h.ChildSeen)
                return;

            GameEntity entity = LiveEntity(role);
            if (entity == null)
                return;

            if (count < h.StuckSeen)
            {
                foreach (MetaMesh old in h.Stuck)
                    entity.RemoveMultiMesh(old);

                h.Stuck.Clear();
                h.StuckLocal.Clear();
                h.StuckSeen = 0;
            }

            for (int i = h.StuckSeen; i < count && h.Stuck.Count < MaxStuck; i++)
            {
                ItemObject item = weapon.GetAttachedWeapon(i).Item;
                string name = item == null ? null
                    : !string.IsNullOrEmpty(item.MultiMeshName) ? item.MultiMeshName : item.FlyingMeshName;
                MetaMesh meta = string.IsNullOrEmpty(name) ? null : MetaMesh.GetCopy(name, false, true);
                if (meta == null)
                    continue;

                MatrixFrame local = weapon.GetAttachedWeaponFrame(i);
                meta.Frame = h.Frame.TransformToParent(local);
                entity.AddMultiMesh(meta, true);
                h.Stuck.Add(meta);
                h.StuckLocal.Add(local);
            }

            // The engine's own copies: every mesh on its slot entity's children.
            // A shield has no children of its own, so whatever is there was stuck.
            int taken = 0;
            for (int c = 0; c < children && c < MaxStuck; c++)
            {
                WeakGameEntity child = source.GetChild(c);
                int metas = child.MultiMeshComponentCount;
                var off = new List<MetaMesh>();
                for (int m = 0; m < metas && m < MaxMeta; m++)
                {
                    MetaMesh meta = child.GetMetaMesh(m);
                    if (meta != null)
                        off.Add(meta);
                }

                foreach (MetaMesh meta in off)
                {
                    child.RemoveMultiMesh(meta);
                    h.StuckOriginals.Add(new KeyValuePair<UIntPtr, MetaMesh>(child.Pointer, meta));
                    taken++;
                }
            }

            if (count != h.StuckSeen || taken > 0)
            {
                VrLog.Info("TEST 305: '" + h.ItemId + "' carries " + count + " stuck missile(s), "
                           + h.Stuck.Count + " drawn on the VR copy; the engine's slot entity has "
                           + children + " child entit(ies), " + taken + " mesh(es) taken off them "
                           + "this time (" + source.MultiMeshComponentCount + " on the entity itself).");
            }

            h.StuckSeen = count;
            h.ChildSeen = children;
        }

        /// <summary>TEST 305: the frame our copy's meshes sit in - the entity
        /// times the per-weapon pose - and the slot it holds. A missile stuck in
        /// the shield is expressed in this frame so the engine's own attach list
        /// and our copy agree on where it is.</summary>
        internal static bool TryMeshWorld(HandRole role, out MatrixFrame meshWorld, out EquipmentIndex slot)
        {
            Held held = _held[(int)role];
            if (held == null || held.Copies.Count == 0)
            {
                meshWorld = MatrixFrame.Identity;
                slot = EquipmentIndex.None;
                return false;
            }

            meshWorld = held.World.TransformToParent(held.Frame);
            slot = held.Slot;
            return true;
        }

        /// <summary>TEST 487: what this role's hand holds - our copies, the item,
        /// its class, and where its meshes sit in the HAND BONE's frame: the
        /// fixed or frozen grip times the per-weapon pose (ItemLocal x Frame).
        /// Static - none of the glide, weight lag or solid turn the drawn frame
        /// carries (483b baked the drawn frame and missed by up to 14 cm). False
        /// until the grip is fixed or frozen.</summary>
        internal static bool TryHeldLocal(HandRole role, out List<MetaMesh> copies, out MatrixFrame itemInHand,
                                          out string itemId, out string weaponClass)
        {
            Held held = _broken ? null : _held[(int)role];
            copies = held?.Copies;
            weaponClass = held?.Class ?? string.Empty;
            itemId = held?.ItemId ?? string.Empty;
            itemInHand = held != null ? held.ItemLocal.TransformToParent(held.Frame) : MatrixFrame.Identity;

            // TEST 560: a crossbow on the palm is not on its pose.
            if (held != null && VrCrossbow.TryPalmLocal(role, weaponClass, out MatrixFrame palm))
                itemInHand = palm;
            return held != null && held.Frozen && held.Copies.Count > 0;
        }

        /// <summary>TEST 480: the grip this role's weapon is placed with - the item
        /// bone against the wrist, fixed (TEST 273) or frozen (TEST 272). False
        /// while it still follows the animation.</summary>
        /// <summary>TEST 495: the role's weapon is drawn by a pose aligned for
        /// five-finger hands (fitem:/fclass:) - VrHandMesh does not seat it.</summary>
        internal static bool FingerPosed(HandRole role)
        {
            Held held = _broken ? null : _held[(int)role];
            return held?.PoseKey != null && IsFingerKey(held.PoseKey);
        }

        private static bool IsFingerKey(string key) => key != null && key.StartsWith("f", StringComparison.Ordinal);

        /// <summary>TEST 495: five-finger hands switched on or off - each held
        /// weapon takes the pose for the hands now drawn.</summary>
        internal static void Repose()
        {
            for (int i = 0; i < Roles; i++)
            {
                Held held = _held[i];
                if (held == null)
                    continue;

                held.PoseKey = ResolvePoseKey(held, i);
                held.Frame = BuildFrame(PoseOf(held.PoseKey) ?? new float[6], held.Class);
                held.Dirty = true;
            }
        }

        /// <summary>TEST 495: an offset in the hand bone's frame as the pose's x/y/z
        /// - the same move once the item's grip (ItemLocal) and the nudge have
        /// turned it. Both are rotations (the grip's scale cancels against the
        /// hand's), so their transposes undo them.</summary>
        internal static Vec3 SeatInPose(Vec3 seat, Mat3 itemLocal, Mat3 nudge)
            => nudge.TransformToLocal(itemLocal.TransformToLocal(seat));

        /// <summary>TEST 499: the hand bone the role's weapon was last placed from,
        /// in world.</summary>
        internal static bool TryHandWorld(HandRole role, out MatrixFrame hand)
        {
            Held held = _broken ? null : _held[(int)role];
            hand = held != null ? held.Hand : MatrixFrame.Identity;
            return held != null && held.HaveHand && held.Copies.Count > 0;
        }

        /// <summary>TEST 498: settled grips, per hand and item - kept for the session
        /// and in the config (skin_item_[id]_grip_[hand], the twelve numbers of
        /// weapon_fixed_grip_main), so the next grab of the item is frozen at once.</summary>
        private static readonly Dictionary<string, MatrixFrame> _settledGrips = new Dictionary<string, MatrixFrame>(StringComparer.Ordinal);

        private static string GripKey(HandRole role, string itemId) => Prefix("item:" + itemId) + "grip_" + RoleName[(int)role];

        private static bool TryRememberedGrip(HandRole role, string itemId, out MatrixFrame grip)
        {
            grip = MatrixFrame.Identity;
            if (itemId.Length == 0)
                return false;

            string key = GripKey(role, itemId);
            if (_settledGrips.TryGetValue(key, out grip))
                return true;

            MatrixFrame? saved = ParseGrip(VrConfig.String(key, string.Empty));
            if (!saved.HasValue)
                return false;

            grip = saved.Value;
            _settledGrips[key] = grip;
            return true;
        }

        private static void RememberGrip(HandRole role, string itemId, MatrixFrame grip)
        {
            if (itemId.Length == 0)
                return;

            string key = GripKey(role, itemId);
            bool known = _settledGrips.ContainsKey(key);
            _settledGrips[key] = grip;
            if (known)
                return;

            Mat3 r = grip.rotation;
            string text = string.Join(",", Array.ConvertAll(new[]
            {
                r.s.x, r.s.y, r.s.z, r.f.x, r.f.y, r.f.z, r.u.x, r.u.y, r.u.z, grip.origin.x, grip.origin.y, grip.origin.z
            }, v => v.ToString("0.0000", CultureInfo.InvariantCulture)));
            VrConfig.SaveToMod(key, text);
            VrLog.Info("TEST 498: '" + itemId + "' settled its grip - kept as " + key + ", the next grab takes it at once.");
        }

        internal static bool TryGripLocal(HandRole role, out MatrixFrame gripLocal)
        {
            Held held = _broken ? null : _held[(int)role];
            gripLocal = held != null ? held.ItemLocal : MatrixFrame.Identity;
            return held != null && held.Frozen && held.Copies.Count > 0;
        }

        private static void Detach(Agent agent, int i, bool restore)
        {
            Held held = _held[i];
            if (held == null)
                return;

            _held[i] = null;

            try
            {
                GameEntity entity = LiveEntity(i);
                if (entity != null)
                {
                    foreach (MetaMesh copy in held.Copies)
                        entity.RemoveMultiMesh(copy);

                    foreach (MetaMesh stuck in held.Stuck)
                        entity.RemoveMultiMesh(stuck);

                    entity.SetVisibilityExcludeParents(false);
                }

                if (agent == null
                    || (!restore && !held.Removed && !held.ClothRemoved)
                    || (int)held.Slot < 0 || (int)held.Slot > 3)
                    return;

                MissionWeapon weapon = agent.Equipment[held.Slot];
                if (weapon.Item == null)
                    return;

                WeakGameEntity source = agent.GetWeaponEntityFromEquipmentSlot(held.Slot);
                if (!source.IsValid || source.Pointer != held.Source)
                    return;

                VisibilityMaskFlags mask = (VisibilityMaskFlags)(restore ? 33 : 0);

                // TEST 305: the stuck missiles go back on whichever of the engine's
                // children still exist - one that is gone (a broken shield) is
                // never touched through a stale pointer.
                if (held.StuckOriginals.Count > 0)
                {
                    int children = source.ChildCount;
                    for (int k = 0; k < children && k < MaxStuck; k++)
                    {
                        WeakGameEntity child = source.GetChild(k);
                        foreach (KeyValuePair<UIntPtr, MetaMesh> taken in held.StuckOriginals)
                        {
                            if (taken.Key != child.Pointer)
                                continue;

                            taken.Value.SetVisibilityMask(mask);
                            Opaque(taken.Value);
                            child.AddMultiMesh(taken.Value, true);
                            taken.Value.SetVisibilityMask(mask);
                        }
                    }
                }

                if (held.Removed)
                {
                    foreach (MetaMesh original in held.Originals)
                    {
                        original.SetVisibilityMask(mask);
                        Opaque(original);
                        source.AddMultiMesh(original, true);
                        original.SetVisibilityMask(mask);
                    }
                }
                else
                {
                    int meshes = source.MultiMeshComponentCount;
                    for (int m = 0; m < meshes && m < MaxMeta; m++)
                    {
                        MetaMesh meta = source.GetMetaMesh(m);
                        if (meta != null)
                            meta.SetVisibilityMask(mask);
                    }
                }

                if (held.ClothRemoved)
                {
                    GameEntity clothEntity = GameEntity.CreateFromWeakEntity(source);
                    if (clothEntity != null)
                    {
                        foreach (ClothSimulatorComponent cloth in held.ClothOriginals)
                            clothEntity.AddComponent(cloth);
                    }
                }

                int cloths = source.ClothSimulatorComponentCount;
                int c = 0;
                while (held.Cloths > 0 && c < cloths && c < held.Cloths)
                {
                    ClothSimulatorComponent cloth = source.GetClothSimulator(c);
                    if (cloth != null)
                    {
                        MetaMesh meta = cloth.GetFirstMetaMesh();
                        if (meta != null)
                            meta.SetVisibilityMask(mask);
                    }

                    c++;
                }

                source.SetVisibilityExcludeParents(true);
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 229: detaching failed.", ex);
            }
        }

        /// <summary>Our entity for this hand, but only while it is still in the
        /// mission the scene belonged to. The accessors behind a dead one fault
        /// rather than throw, so the scene pointer is checked before any use.</summary>
        private static GameEntity LiveEntity(int i)
        {
            GameEntity entity = _entity[i];
            if (entity == null)
                return null;

            Mission mission = Mission.Current;
            if (mission == null || mission != _mission[i])
                return null;

            return entity;
        }

        /// <summary>
        /// The visual half of a held item, for the physical combat pass: the
        /// entity frame this tick's Drive wrote, the metameshes that make the
        /// weapon, and a stamp that changes when the meshes or the pose do.
        /// False whenever Drive declined - the caller then measures the engine's
        /// own entity instead. See VrBlade.
        /// </summary>
        internal static bool TryGetVisual(HandRole role, out MatrixFrame world,
                                          out List<MetaMesh> meshes, out int stamp,
                                          out string itemId)
        {
            world = MatrixFrame.Identity;
            meshes = null;
            stamp = 0;
            itemId = null;

            Held held = _held[(int)role];
            if (!Enabled || _broken || held == null || held.Copies.Count == 0)
                return false;

            world = held.World;
            meshes = held.Copies;
            stamp = held.BladeStamp;
            itemId = held.ItemId;
            return true;
        }

        /// <summary>A hand that stopped holding: give the item back, nothing more.</summary>
        public static void Drop(Agent agent, HandRole role)
        {
            if (Enabled && !_broken)
                Detach(agent, (int)role, restore: false);
        }

        /// <summary>Everything back to the engine - the switch going off, or the
        /// mission ending. Restores even a mask-only hold.</summary>
        public static void Release(Agent agent)
        {
            if (_held[0] != null || _held[1] != null)
            {
                Detach(agent, 0, restore: true);
                Detach(agent, 1, restore: true);
            }
        }

        /// <summary>
        /// TEST 324: the engine is about to drop this slot, and the entity it drops
        /// IS the slot entity we stripped (Mission.SpawnWeaponAsDropFromAgentAux
        /// turns it into the ground item). Put its meshes back, visible, while the
        /// slot still holds it - Detach's own checks refuse once the slot is empty,
        /// which is why a G-drop used to lie invisible. True if a hand held it.
        /// </summary>
        internal static bool RestoreSlot(Agent agent, EquipmentIndex slot)
        {
            for (int i = 0; i < Roles; i++)
            {
                Held held = _held[i];
                if (held != null && held.Slot == slot && _mission[i] == Mission.Current)
                {
                    Detach(agent, i, restore: true);
                    return true;
                }
            }

            return false;
        }

        /// <summary>TEST 269d: shows or hides a hand's item entity, for VrDrawnBow,
        /// which draws the bow itself while an arrow is nocked. Nothing else here
        /// touches the entity's visibility after Attach.</summary>
        /// <summary>TEST 569: the pose the crossbow's palm hold (TEST 560) stands
        /// in for - fclass:Crossbow as it was. The tool's pose is applied as its
        /// change from this, so the tool moves a palm-held crossbow too.</summary>
        private static readonly float[] PalmRef = ParseSix(
            VrConfig.String("xbow_palm_ref_pose", "0.344,-0.013,0.018,-24.4,70.4,-68.0"));

        private static float[] ParseSix(string text)
        {
            string[] parts = text.Split(',');
            if (parts.Length != 6)
                return null;

            var v = new float[6];
            for (int i = 0; i < 6; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
                    return null;
            return v;
        }

        /// <summary>TEST 569: this role's pose against xbow_palm_ref_pose, in its
        /// hand bone's frame (ItemLocal x Frame x Ref^-1 x ItemLocal^-1); identity
        /// while the crossbow is on the reference pose.</summary>
        internal static MatrixFrame PoseShift(HandRole role)
        {
            Held held = _broken ? null : _held[(int)role];
            if (held == null || PalmRef == null || held.PoseKey == null)
                return MatrixFrame.Identity;

            MatrixFrame change = held.Frame.TransformToParent(
                BuildFrame(PalmRef, held.Class).TransformToLocal(MatrixFrame.Identity));
            return held.ItemLocal.TransformToParent(change)
                .TransformToParent(held.ItemLocal.TransformToLocalNonOrthogonal(MatrixFrame.Identity));
        }

        /// <summary>TEST 568: whether this role's copy is drawn now.</summary>
        internal static bool IsShown(HandRole role)
        {
            GameEntity entity = _entity[(int)role];
            return _held[(int)role] != null && entity != null && entity.IsVisibleIncludeParents();
        }

        internal static void SetShown(HandRole role, bool shown)
        {
            int i = (int)role;
            if (_held[i] != null && _entity[i] != null)
                _entity[i].SetVisibilityExcludeParents(shown);
        }

        public static void Reset()
        {
            for (int i = 0; i < Roles; i++)
            {
                _held[i] = null;
                _entity[i] = null;
                _mission[i] = null;
            }

            _loggedFirst = false;
            _reshown = 0;
            _readded = 0;
            _clothReadded = 0;
            _ticks = 0;
        }

        /// <summary>Item id and weapon class, for the item:/class: pose lookup.
        /// Never throws: an unreadable item is simply "no pose saved".</summary>
        private static void Identify(Agent agent, Held h)
        {
            try
            {
                MissionWeapon weapon = agent.Equipment[h.Slot];
                if (!weapon.IsEmpty)
                {
                    ItemObject item = weapon.Item;
                    h.ItemId = item?.StringId ?? string.Empty;

                    WeaponComponentData usage = weapon.CurrentUsageItem;
                    if (usage != null)
                    {
                        h.Class = usage.WeaponClass.ToString();
                        h.Ranged = usage.IsBow || usage.IsCrossBow;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The live tuner (skin_tune = 1), on the keys named in the config.
        /// Numpad0 tool MOVE/ROTATE, NumpadMultiply hand, NumpadSlash CLASS/ITEM,
        /// arrows / PageUp / PageDown on the axes, Numpad5 zero, NumpadEnter
        /// save - the layout TEST 231 settled on when the first one collided
        /// with the game's own keys. See the journal for the full map.
        /// </summary>
        public static void TuneTick()
        {
            if (!Enabled || !Tune || _broken)
                return;

            try
            {
                // The whole tool sits behind one key and starts OFF, so arrows and
                // the numpad do nothing until it is asked for.
                if (VrKeys.Pressed(KeyEnable))
                {
                    _tuneOn = !_tuneOn;
                    Feed(_tuneOn
                             ? "Weapon/hand tune ON (" + KeyEnable + " to turn off)."
                             : "Weapon/hand tune OFF (" + KeyEnable + " to turn on).",
                         _tuneOn ? Colors.Green : Colors.Gray);
                }

                if (!_tuneOn)
                {
                    _tuneStamp = 0L;    // so turning it on starts with dt 0 and a Say()
                    return;
                }

                if (_tuneStamp == 0L)
                    Say();

                long now = Stopwatch.GetTimestamp();
                float dt = _tuneStamp == 0L
                    ? 0f
                    : (float)(now - _tuneStamp) / Stopwatch.Frequency;
                _tuneStamp = now;

                if (dt > 0.1f)
                    dt = 0.1f;

                if (VrKeys.Pressed(KeyHand))
                {
                    _tuneRole = 1 - _tuneRole;
                    Say();
                }

                if (VrKeys.Pressed(KeyTool))
                {
                    _tuneRotate = !_tuneRotate;
                    Say();
                }

                if (VrKeys.Pressed(KeyScope))
                {
                    _tuneItemScope = !_tuneItemScope;
                    Say();
                }

                if (VrKeys.Pressed(KeyGrip))
                {
                    _tuneGrip = !_tuneGrip;
                    Say();
                }

                bool zero = VrKeys.Pressed(KeyZero);
                bool save = VrKeys.Pressed(KeySave);
                float step = (VrKeys.Down(KeyFine) ? 0.25f : 1f) * dt;
                float rate = (_tuneRotate ? 40f : 0.1f) * step;
                int axis = _tuneRotate ? 3 : 0;

                float dx = Axis(KeyXPlus, KeyXMinus);
                float dy = Axis(KeyYPlus, KeyYMinus);
                float dz = Axis(KeyZPlus, KeyZMinus);
                bool nudged = dx != 0f || dy != 0f || dz != 0f;

                if (!zero && !save && !nudged)
                    return;

                // HAND mode: the same keys drive the grip sliders VrHandCalibration
                // owns - offset in the body frame, turn about the hand's own axes -
                // so no weapon has to be held to align a hand.
                if (_tuneGrip)
                {
                    TuneGrip((HandRole)_tuneRole, zero, save, rate, dx, dy, dz);
                    return;
                }

                Held held = _held[_tuneRole];
                if (held == null || held.Copies.Count == 0)
                {
                    Feed("Weapon tune: the " + RoleName[_tuneRole].ToUpperInvariant()
                         + " hand is holding nothing to align.", Colors.Red);
                    return;
                }

                string key = TargetKey(held);
                if (key == null)
                {
                    Feed("Weapon tune: this item has no id or class to save under.", Colors.Red);
                    return;
                }

                float[] pose = PoseForEdit(held, key);

                if (zero)
                {
                    for (int i = 0; i < Dof; i++)
                        pose[i] = 0f;

                    Apply(key, pose, logNow: true);
                }
                else if (save)
                {
                    Save(held, key, pose);
                }
                else
                {
                    pose[axis] += dx * rate;
                    pose[axis + 1] += dy * rate;
                    pose[axis + 2] += dz * rate;
                    Apply(key, pose, logNow: false);
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 231 tune failed; live tuning is off for this session.", ex);
                _tuneStamp = 0L;
            }
        }

        private static void TuneGrip(HandRole role, bool zero, bool save,
                                     float rate, float dx, float dy, float dz)
        {
            if (save)
            {
                VrHandCalibration.SaveOne(role);
                Feed("Hand tune: " + role + " hand grip saved for " + VrHandCalibration.ControllerName + ".", Colors.Green);
                VrLog.Info("Hand tune: " + role + " hand grip saved for " + VrHandCalibration.ControllerName + ".");
                return;
            }

            VrHandCalibration.Adjustment(role, out Vec3 offset, out Vec3 euler);

            if (zero)
            {
                offset = Vec3.Zero;
                euler = Vec3.Zero;
            }
            else if (_tuneRotate)
            {
                euler += new Vec3(dx, dy, dz) * rate;
            }
            else
            {
                offset += new Vec3(dx, dy, dz) * rate;
            }

            VrHandCalibration.SetAdjustment(role, offset, euler);

            double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            if (!zero && now < _tuneLogAt)
                return;

            _tuneLogAt = now + 0.5;
            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "Hand tune {0}: offset ({1:0.000}, {2:0.000}, {3:0.000}) m, "
                + "yaw {4:0.0} pitch {5:0.0} roll {6:0.0} deg",
                role, offset.x, offset.y, offset.z, euler.x, euler.y, euler.z));
        }

        /// <summary>TEST 495: with five-finger hands on, the tool edits and saves
        /// their own poses (fitem:, fclass:) - the game hands keep theirs.</summary>
        private static string TargetKey(Held h)
        {
            string f = VrHandMesh.Enabled ? "f" : string.Empty;
            if (_tuneItemScope)
                return h.ItemId.Length != 0 ? f + "item:" + h.ItemId : null;

            return h.Class.Length != 0 ? f + "class:" + h.Class : null;
        }

        /// <summary>The pose being edited, created from what the item is already
        /// using on first touch so nothing jumps, and never shared with another
        /// weapon.</summary>
        private static float[] PoseForEdit(Held h, string key)
        {
            float[] pose = PoseOf(key);
            if (pose != null)
            {
                h.PoseKey = key;
                return pose;
            }

            float[] current = PoseOf(h.PoseKey);
            pose = StartPose(h, key, _tuneRole);
            if (IsFingerKey(key) && !IsFingerKey(h.PoseKey))
                VrLog.Info("TEST 495 tune: '" + h.ItemId + "' gets a five-finger pose under " + key
                           + ", its seat folded in.");

            _poses[key] = pose;
            h.PoseKey = key;

            VrLog.Info("TEST 231 tune: '" + h.ItemId + "' now has its own pose under " + key
                       + ", started from " + (current != null ? "what it was using" : "zero") + ".");

            return pose;
        }

        /// <summary>A new pose for `key`, started from what the item is drawn with
        /// now so nothing jumps. Not stored.</summary>
        private static float[] StartPose(Held h, string key, int role)
        {
            float[] current = PoseOf(h.PoseKey);
            float[] pose = new float[6];
            if (current != null)
                Array.Copy(current, pose, Dof);

            // TEST 495: a five-finger pose has no automatic seat (VrHandMesh), so it
            // starts with the seat the weapon is drawn with folded in - nothing jumps,
            // and what is aligned is what is drawn and saved.
            if (IsFingerKey(key) && !IsFingerKey(h.PoseKey))
            {
                Mat3 nudge = VrWeaponEntity.HaveNudge ? VrWeaponEntity.NudgeFrame.rotation : Mat3.Identity;
                Vec3 seat = SeatInPose(VrHandMesh.SeatOf((HandRole)role), h.ItemLocal.rotation, nudge);
                pose[0] += seat.x;
                pose[1] += seat.y;
                pose[2] += seat.z;
            }

            return pose;
        }

        // --- TEST 750: the VR panel's Calibration tool ---------------------------
        // Live edits always go to the held ITEM's own pose, so a slider never moves
        // another weapon; the Save buttons then pick the item or its class. The last
        // weapon each hand held is kept after a drop, so it can still be saved.
        private static readonly Held[] _toolLast = new Held[Roles];
        private static readonly HashSet<string> _toolDirty = new HashSet<string>(StringComparer.Ordinal);

        private static string ToolItemKey(Held h)
        {
            return h.ItemId.Length == 0 ? null : (VrHandMesh.Enabled ? "f" : string.Empty) + "item:" + h.ItemId;
        }

        /// <summary>What the sliders show for this hand. 1 = held now, 0 = dropped
        /// (the last one can still be saved), -1 = nothing to edit.</summary>
        internal static int ToolSnapshot(int role, float[] values, out string label)
        {
            try
            {
                return Snapshot(role, values, out label);
            }
            catch (Exception ex)
            {
                if (!_toolSnapshotLogged)
                {
                    _toolSnapshotLogged = true;
                    VrLog.Error("TEST 750 calibration tool: reading the held weapon failed.", ex);
                }
                Array.Clear(values, 0, Dof);
                label = "Could not read the held weapon - see the log.";
                return -1;
            }
        }

        private static bool _toolSnapshotLogged;

        private static int Snapshot(int role, float[] values, out string label)
        {
            Array.Clear(values, 0, Dof);

            if (!Enabled || _broken)
            {
                label = "Weapon alignment needs the mod's own weapon drawing (weapon_skinned = 1).";
                return -1;
            }

            Held now = _held[role];
            if (now != null && now.Copies.Count != 0)
                _toolLast[role] = now;
            else
                now = null;

            Held h = _toolLast[role];
            if (h == null)
            {
                label = "Pick up a weapon in your " + RoleName[role] + " hand.";
                return -1;
            }

            string key = ToolItemKey(h);
            float[] pose = (key != null ? PoseOf(key) : null)
                           ?? (key != null ? StartPose(h, key, role) : PoseOf(h.PoseKey));
            if (pose != null)
                Array.Copy(pose, values, Dof);

            string item = h.ItemId + " (" + h.Class + ")";
            if (now == null)
            {
                label = "Dropped: " + item + ". Save it, or pick it up to edit.";
                return 0;
            }

            string from = h.PoseKey == null ? "no pose yet"
                : h.PoseKey.StartsWith("role:", StringComparison.Ordinal) ? "the hand's default pose"
                : h.PoseKey.IndexOf("class:", StringComparison.Ordinal) >= 0 ? "its CLASS pose"
                : "its own ITEM pose";
            bool unsaved = key != null && _toolDirty.Contains(key);
            label = "Holding " + item + " - " + from + (unsaved ? ", NOT SAVED" : string.Empty);
            return 1;
        }

        /// <summary>Carries out one panel command for this hand's weapon; returns
        /// the line the panel shows, or null to leave it as it is.</summary>
        internal static string ToolCommand(int role, BvrToolCommand command, float[] values)
        {
            if (!Enabled || _broken || command == BvrToolCommand.None)
                return null;

            try
            {
                Held now = _held[role] != null && _held[role].Copies.Count != 0 ? _held[role] : null;
                Held h = command == BvrToolCommand.SaveItem || command == BvrToolCommand.SaveClass
                    ? _toolLast[role] : now;
                if (h == null)
                    return "Nothing in that hand to align.";

                string itemKey = ToolItemKey(h);
                switch (command)
                {
                    case BvrToolCommand.Adjust:
                    case BvrToolCommand.Zero:
                        if (itemKey == null)
                            return "This weapon has no id to edit under.";
                        float[] pose = PoseOf(itemKey);
                        if (pose == null)
                        {
                            pose = new float[Dof];
                            _poses[itemKey] = pose;
                        }
                        if (command == BvrToolCommand.Zero)
                            Array.Clear(pose, 0, Dof);
                        else
                            Array.Copy(values, pose, Dof);
                        h.PoseKey = itemKey;
                        _toolDirty.Add(itemKey);
                        Apply(itemKey, pose, logNow: command == BvrToolCommand.Zero);
                        return command == BvrToolCommand.Zero ? "Zeroed - not saved yet." : null;

                    case BvrToolCommand.Revert:
                        if (itemKey != null)
                        {
                            _poses.Remove(itemKey);     // the next read comes from the config
                            _toolDirty.Remove(itemKey);
                        }
                        Refresh();
                        return "Back to the saved pose.";

                    case BvrToolCommand.SaveItem:
                    {
                        if (itemKey == null)
                            return "This weapon has no id to save under.";
                        float[] itemPose = PoseOf(itemKey);
                        if (itemPose == null)
                        {
                            itemPose = new float[Dof];
                            _poses[itemKey] = itemPose;
                        }
                        Array.Copy(values, itemPose, Dof);
                        Save(h, itemKey, itemPose);
                        _toolDirty.Remove(itemKey);
                        Refresh();
                        return "Saved to this item: " + h.ItemId + " (" + ProfileName + ").";
                    }

                    case BvrToolCommand.SaveClass:
                    {
                        if (h.Class.Length == 0)
                            return "This weapon has no class to save under.";
                        // An unsaved item pose is only the tool's working copy: drop it
                        // so the class takes over. A SAVED item pose outranks its class,
                        // so Save writes it too (TEST 271b) - what is seen is what stays.
                        if (itemKey != null && LoadPose(itemKey) == null)
                            _poses.Remove(itemKey);
                        string classKey = (VrHandMesh.Enabled ? "f" : string.Empty) + "class:" + h.Class;
                        float[] classPose = PoseOf(classKey);
                        if (classPose == null)
                        {
                            classPose = new float[Dof];
                            _poses[classKey] = classPose;
                        }
                        Array.Copy(values, classPose, Dof);
                        Save(h, classKey, classPose);
                        if (itemKey != null)
                            _toolDirty.Remove(itemKey);
                        Refresh();
                        return "Saved to the class " + h.Class + " (" + ProfileName + ").";
                    }
                }
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 750 calibration tool failed on " + command + ".", ex);
                return "That failed - see the log.";
            }

            return null;
        }

        /// <summary>Every held weapon re-resolves its pose, after a save or a
        /// revert changed which one wins.</summary>
        private static void Refresh()
        {
            for (int i = 0; i < Roles; i++)
            {
                Held held = _held[i];
                if (held == null)
                    continue;
                held.PoseKey = ResolvePoseKey(held, i);
                held.Frame = BuildFrame(PoseOf(held.PoseKey) ?? new float[6], held.Class);
                held.Dirty = true;
            }
        }

        private static void Say()
        {
            if (_tuneGrip)
            {
                string grip = "Hand tune: " + RoleName[_tuneRole].ToUpperInvariant() + " HAND grip (" + VrHandCalibration.ControllerName + "), "
                              + (_tuneRotate
                                  ? "ROTATE (" + KeyXPlus + "/" + KeyXMinus + " yaw, " + KeyYPlus + "/"
                                    + KeyYMinus + " pitch, " + KeyZPlus + "/" + KeyZMinus + " roll)"
                                  : "MOVE (" + KeyXPlus + "/" + KeyXMinus + " side, " + KeyYPlus + "/"
                                    + KeyYMinus + " forward, " + KeyZPlus + "/" + KeyZMinus + " up)")
                              + ". " + KeyGrip + " back to the weapon.";

                VrLog.Info(grip);
                Feed(grip, Colors.Magenta);
                return;
            }

            Held held = _held[_tuneRole];
            string item = held == null || held.Copies.Count == 0
                ? "nothing held"
                : held.ItemId + " (" + held.Class + ")";

            string text = "Weapon tune: " + RoleName[_tuneRole].ToUpperInvariant() + " hand, " + item
                          + " - saving to " + (_tuneItemScope ? "this ITEM" : "its CLASS")
                          + (VrHandMesh.Enabled ? " for five-finger hands" : string.Empty) + ", "
                          + (_tuneRotate
                              ? "ROTATE (" + KeyXPlus + "/" + KeyXMinus + " yaw, " + KeyYPlus + "/"
                                + KeyYMinus + " pitch, " + KeyZPlus + "/" + KeyZMinus + " roll)"
                              : "MOVE (" + KeyXPlus + "/" + KeyXMinus + " x, " + KeyYPlus + "/"
                                + KeyYMinus + " y, " + KeyZPlus + "/" + KeyZMinus + " z)");

            VrLog.Info("TEST 231 tune: " + text);
            Feed(text, _tuneRotate ? Colors.Yellow : Colors.Cyan);
        }

        private static void Feed(string text, Color color)
        {
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(text, color));
            }
            catch (Exception)
            {
            }
        }

        private static float Axis(string plus, string minus)
        {
            return (VrKeys.Down(plus) ? 1f : 0f) - (VrKeys.Down(minus) ? 1f : 0f);
        }

        private static void Apply(string key, float[] pose, bool logNow)
        {
            for (int i = 0; i < Roles; i++)
            {
                Held held = _held[i];
                if (held != null && held.PoseKey == key)
                {
                    held.Frame = BuildFrame(pose, held.Class);
                    held.Dirty = true;
                }
            }

            double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            if (!logNow && now < _tuneLogAt)
                return;

            _tuneLogAt = now + 0.5;
            VrLog.Info("TEST 231 tune " + key + ": offset ("
                       + pose[0].ToString("0.000", CultureInfo.InvariantCulture) + ", "
                       + pose[1].ToString("0.000", CultureInfo.InvariantCulture) + ", "
                       + pose[2].ToString("0.000", CultureInfo.InvariantCulture) + ") m, "
                       + "yaw/pitch/roll ("
                       + pose[3].ToString("0.0", CultureInfo.InvariantCulture) + ", "
                       + pose[4].ToString("0.0", CultureInfo.InvariantCulture) + ", "
                       + pose[5].ToString("0.0", CultureInfo.InvariantCulture) + ") deg.");
        }

        /// <summary>Writes this weapon's six numbers under its item: or class:
        /// key. VrConfig.Save rewrites every assignment of a key, so the last
        /// value wins in the file.</summary>
        private static void Save(Held h, string key, float[] pose)
        {
            string prefix = PosePrefix(key, _profile);
            var line = new StringBuilder();

            for (int i = 0; i < Dof; i++)
            {
                string name = prefix + DofName[i];
                string value = pose[i].ToString(i < 3 ? "0.000" : "0.0",
                                               CultureInfo.InvariantCulture);

                VrConfig.SaveToMod(name, value);
                line.Append(name).Append(" = ").Append(value).Append("; ");
            }

            VrLog.Info("TEST 231 tune: written to the config - " + line);
            Feed("Weapon tune: saved " + key + " (" + h.ItemId + ") to the config.", Colors.Green);

            // TEST 271b: an item's own pose outranks its class on every draw, so a
            // class save on an item that has one looked saved and then came back
            // wrong (empire_sword_4_t4, 2026-09-26). What was on screen goes to both.
            // TEST 495: within its family - fclass: to fitem:.
            string family = IsFingerKey(key) ? "f" : string.Empty;
            string itemKey = family + "item:" + h.ItemId;
            float[] itemPose = key.StartsWith(family + "class:", StringComparison.Ordinal) && h.ItemId.Length != 0
                ? PoseOf(itemKey) : null;
            if (itemPose != null && itemPose != pose)
            {
                Array.Copy(pose, itemPose, Dof);
                Save(h, itemKey, itemPose);
            }
        }
    }
}
