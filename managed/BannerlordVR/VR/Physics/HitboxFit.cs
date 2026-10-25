using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR's body capsules (VrPhysicalCombat.Fill) have one radius
    // per body part; measured on imperial veterans the drawn body stood
    // outside them on 57-63% of its surface: thighs 7 cm for 18 cm drawn
    // (tunic and armour skirt), upper arms 7 for 10-13, chest 15 for 19-21,
    // neck 6 for 10. Each capsule grows to the drawn surface's median round
    // its bone (HitboxProbe.Build, from the rest-pose mesh WoundDecal reads),
    // never shrinks, and grows at most MaxGrow for its part, so loose cloth
    // does not swell the target. The head keeps its sphere and the helmet's
    // margin (HelmetVolume). Fitted once per outfit (troop and armour worn)
    // and reused; applied in Fill, so VrCollide and the blade hits both use
    // it. A body whose capsules do not match the measured ones bone for bone
    // keeps BannerlordVR's.
    internal static class HitboxFit
    {
        private const int MinVertices = 8, FitStride = 4;
        private const float Over = .005f;
        private static float MaxGrow(BoneBodyPartType part)
        {
            switch (part)
            {
                case BoneBodyPartType.Legs: return .06f;
                case BoneBodyPartType.Chest:
                case BoneBodyPartType.Abdomen:
                case BoneBodyPartType.ShoulderLeft:
                case BoneBodyPartType.ShoulderRight: return .05f;
                case BoneBodyPartType.Neck: return .03f;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: return .02f;
                default: return 0;
            }
        }

        private sealed class Fit { public sbyte[] Bones; public float[] Radii; }
        private static readonly Dictionary<string, Fit> Fits = new Dictionary<string, Fit>();
        private static readonly Dictionary<Agent, string> Keys = new Dictionary<Agent, string>();
        private static Mission _mission;
        private static bool _broken;

        private static string KeyOf(Agent agent)
        {
            if (Keys.TryGetValue(agent, out string key)) return key;
            var s = new System.Text.StringBuilder(agent.Character?.StringId ?? "?");
            try
            {
                Equipment e = agent.SpawnEquipment;
                foreach (EquipmentIndex slot in new[] { EquipmentIndex.Head, EquipmentIndex.Body, EquipmentIndex.Leg, EquipmentIndex.Gloves, EquipmentIndex.Cape })
                    s.Append('|').Append(e == null || e[slot].IsEmpty ? "-" : e[slot].Item?.StringId);
            }
            catch (Exception) { }
            key = s.ToString();
            Keys[agent] = key;
            return key;
        }

        private static void NewMission()
        {
            if (Mission.Current == _mission) return;
            _mission = Mission.Current; Keys.Clear();
        }

        // Measured in battle, each armour mesh copied to be read was an 8-9 ms
        // frame, one every 0.3-0.5 s for as long as new outfits came (a
        // battle froze for half a minute, 37 ms at worst). The radii below
        // come from six outfits measured so (imperial veterans with mail,
        // lamellar or padding, imperial archers in leather), a little under
        // the drawn medians, by the capsule's parent bone; the reading stays
        // behind enemy_physics_fit_hitbox_measure.
        private static float Generic(string bone)
        {
            switch (bone)
            {
                case "l_thigh": case "r_thigh": return .12f;          // drawn 17.8, capped 13
                case "pelvis": return .15f;                            // hips 15.0-15.3, belly 16.9
                case "spine": return .16f;                             // 16.4
                case "spine1": return .19f;                            // 18.8-21.4
                case "l_clavicle": case "r_clavicle": return .11f;     // 13.0
                case "l_upperarm_twist": case "r_upperarm_twist": return .10f; // 10.0-12.2
                case "neck": return .085f;                             // 9.5-10.4
                default: return 0;
            }
        }
        private static readonly Dictionary<Monster, float[]> GenericByBone = new Dictionary<Monster, float[]>();
        private static readonly float[] Radii = new float[65];
        private static float[] GenericFor(Agent agent)
        {
            Monster monster = agent.Monster;
            if (monster == null) return null;
            if (GenericByBone.TryGetValue(monster, out float[] byBone)) return byBone;
            byBone = null;
            try
            {
                TaleWorlds.Engine.Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                if (skeleton != null)
                {
                    int count = Math.Min(64, (int)skeleton.GetBoneCount());
                    byBone = new float[64];
                    for (sbyte b = 0; b < count; b++) byBone[b] = Generic(skeleton.GetBoneName(b) ?? "");
                }
            }
            catch (Exception) { byBone = null; }
            GenericByBone[monster] = byBone;
            return byBone;
        }

        // Whether this agent's outfit still needs its surface read.
        public static bool Wants(Agent agent)
        {
            if (!EnemyPhysicsSettings.FitHitbox || !EnemyPhysicsSettings.FitHitboxMeasure || _broken || agent == null || !agent.IsHuman) return false;
            NewMission();
            return !Fits.ContainsKey(KeyOf(agent));
        }

        // Fits the outfits whose surface has been read.
        public static void Tick()
        {
            if (!EnemyPhysicsSettings.FitHitbox || !EnemyPhysicsSettings.FitHitboxMeasure || _broken) return;
            NewMission();
            foreach (KeyValuePair<Agent, List<Vec3>> pair in WoundDecal.ReadBodies())
            {
                string key = KeyOf(pair.Key);
                if (Fits.ContainsKey(key)) continue;
                Fits[key] = null; // tried, whatever comes of it
                try
                {
                    // One vertex in four (a median needs no more), one outfit
                    // a frame: all of a body against 28 capsules was a 5-11 ms
                    // frame.
                    if (!HitboxProbe.Build(pair.Key, pair.Value, out List<HitboxProbe.Capsule> capsules, out List<float>[] distances, out _, FitStride)) return;
                    var fit = new Fit { Bones = new sbyte[capsules.Count], Radii = new float[capsules.Count] };
                    var grown = new System.Text.StringBuilder();
                    int widened = 0;
                    for (int i = 0; i < capsules.Count; i++)
                    {
                        HitboxProbe.Capsule c = capsules[i];
                        fit.Bones[i] = c.Bone;
                        List<float> d = distances[i];
                        // The chest's capsules that end at the neck or a
                        // clavicle are not widened: their rounded end round
                        // the neck's base would take a cut at the neck (the
                        // neck cut wants the neck part, and the blow counted
                        // as a chest's).
                        string child = c.Name.Substring(c.Name.IndexOf('>') + 1);
                        float grow = c.Part == BoneBodyPartType.Chest && (child.Contains("neck") || child.Contains("clavicle")) ? 0 : MaxGrow(c.Part);
                        if (grow <= 0 || d.Count < MinVertices) continue;
                        float median = d[d.Count / 2];
                        float r = Math.Min(c.Radius + grow, median + Over);
                        if (r <= c.Radius + .005f) continue;
                        fit.Radii[i] = r; widened++;
                        if (grown.Length < 400)
                            grown.Append(' ').Append(c.Name).Append(' ').Append((c.Radius * 100).ToString("F0", CultureInfo.InvariantCulture))
                                 .Append("->").Append((r * 100).ToString("F0", CultureInfo.InvariantCulture)).Append(';');
                    }
                    Fits[key] = fit;
                    PhysicsLog.Info("HITBOX_FIT outfit " + key + ": " + widened + " of " + capsules.Count + " capsules widened to the drawn body (cm):" + grown);
                }
                catch (Exception ex) { PhysicsLog.Error("Fitting the hitbox of " + key + " failed; it keeps BannerlordVR's.", ex); }
                return;
            }
        }

        // Fill's postfix: the fitted radii on this agent's capsules, if its
        // capsules are the measured ones, bone for bone.
        private static readonly sbyte[] Seen = new sbyte[65];
        private static void Apply(object body, Agent agent)
        {
            if (!EnemyPhysicsSettings.FitHitbox || _broken || body == null || agent == null || !agent.IsHuman) return;
            NewMission();
            if (!Compiled(body.GetType())) return;
            int count = _bones(body, Seen);
            if (EnemyPhysicsSettings.FitHitboxMeasure && Fits.TryGetValue(KeyOf(agent), out Fit fit) && fit != null)
            {
                if (count != fit.Bones.Length) return;
                for (int i = 0; i < count; i++) if (Seen[i] != fit.Bones[i]) return;
                _widen(body, fit.Radii);
                return;
            }
            // The measured outfits' radii, by each capsule's parent bone; the
            // chest's capsules from spine2 (to the neck and the clavicles)
            // are never in the table.
            float[] byBone = GenericFor(agent);
            if (byBone == null) return;
            for (int i = 0; i < Radii.Length; i++) Radii[i] = i < count && Seen[i] >= 0 && Seen[i] < byBone.Length ? byBone[Seen[i]] : 0;
            _widen(body, Radii);
        }

        [HarmonyPatch]
        internal static class FillPatch
        {
            public static bool Prepare() => EnemyPhysicsSettings.Configured && (AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat") != null);
            public static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat"), "Fill");
            [HarmonyPostfix]
            public static void Postfix(object body, Agent agent)
            {
                if (!EnemyPhysicsSettings.Enabled) return;
                try { Apply(body, agent); }
                catch (Exception ex) { _broken = true; PhysicsLog.Error("Fitted hitboxes failed; BannerlordVR's capsules are used.", ex); }
            }
        }

        // Compiled once: reading and writing BannerlordVR's capsule structs
        // through reflection boxed every capsule of up to 16 bodies a frame.
        private delegate int BonesFn(object body, sbyte[] bones);
        private delegate void WidenFn(object body, float[] radii);
        private static BonesFn _bones;
        private static WidenFn _widen;
        private static bool _built;
        private static bool Compiled(Type bodyType)
        {
            if (_built) return _bones != null && _widen != null;
            _built = true;
            try
            {
                Type capsule = AccessTools.Inner(AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat"), "Capsule");
                if (capsule == null || AccessTools.Field(bodyType, "Count") == null || AccessTools.Field(bodyType, "Capsules") == null ||
                    AccessTools.Field(capsule, "Radius") == null || AccessTools.Field(capsule, "Bone") == null)
                { PhysicsLog.Info("HITBOX_FIT BannerlordVR's capsules are not where they were; its radii are kept."); return false; }
                ParameterExpression raw = Expression.Parameter(typeof(object), "body");
                Expression body = Expression.Convert(raw, bodyType);
                ParameterExpression caps = Expression.Variable(capsule.MakeArrayType(), "caps"), c = Expression.Variable(capsule, "c");
                ParameterExpression i = Expression.Variable(typeof(int), "i"), n = Expression.Variable(typeof(int), "n");
                MethodInfo min = typeof(Math).GetMethod("Min", new[] { typeof(int), typeof(int) });

                // n = min(Count, Capsules.Length, bones.Length); bones[i] = Capsules[i].Bone; return n
                ParameterExpression bones = Expression.Parameter(typeof(sbyte[]), "bones");
                LabelTarget brk1 = Expression.Label();
                BlockExpression read = Expression.Block(typeof(int), new[] { caps, i, n },
                    Expression.Assign(caps, Expression.Field(body, "Capsules")),
                    Expression.Assign(n, Expression.Call(min, Expression.Call(min, Expression.Field(body, "Count"), Expression.ArrayLength(caps)), Expression.ArrayLength(bones))),
                    Expression.Assign(i, Expression.Constant(0)),
                    Expression.Loop(Expression.IfThenElse(Expression.LessThan(i, n), Expression.Block(
                        Expression.Assign(Expression.ArrayAccess(bones, i), Expression.Field(Expression.ArrayIndex(caps, i), "Bone")),
                        Expression.PostIncrementAssign(i)), Expression.Break(brk1)), brk1),
                    n);
                _bones = Expression.Lambda<BonesFn>(read, raw, bones).Compile();

                // for i < min(Count, Capsules.Length, radii.Length): if (radii[i] > c.Radius) c.Radius = radii[i]
                ParameterExpression radii = Expression.Parameter(typeof(float[]), "radii");
                LabelTarget brk2 = Expression.Label();
                Expression radius = Expression.Field(c, "Radius");
                BlockExpression widen = Expression.Block(new[] { caps, c, i, n },
                    Expression.Assign(caps, Expression.Field(body, "Capsules")),
                    Expression.Assign(n, Expression.Call(min, Expression.Call(min, Expression.Field(body, "Count"), Expression.ArrayLength(caps)), Expression.ArrayLength(radii))),
                    Expression.Assign(i, Expression.Constant(0)),
                    Expression.Loop(Expression.IfThenElse(Expression.LessThan(i, n), Expression.Block(
                        Expression.Assign(c, Expression.ArrayIndex(caps, i)),
                        Expression.IfThen(Expression.GreaterThan(Expression.ArrayIndex(radii, i), radius), Expression.Block(
                            Expression.Assign(radius, Expression.ArrayIndex(radii, i)), Expression.Assign(Expression.ArrayAccess(caps, i), c))),
                        Expression.PostIncrementAssign(i)), Expression.Break(brk2)), brk2));
                _widen = Expression.Lambda<WidenFn>(widen, raw, radii).Compile();
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("Compiling the fitted hitboxes failed; BannerlordVR's capsules are used.", ex);
                return false;
            }
        }
    }
}
