using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR's body obstacles (VrPhysicalCombat.Fill) are capsules on
    // the bones: the head's wraps the bare head, so a blade drawn against an
    // enemy went into the helmet before it met anything. B&S helmets are
    // colliders on the head part. With a head armour on, the head and neck
    // capsules grow by the helmet's thickness, for VrCollide and the
    // physics' drawn-bone tests alike.
    internal static class HelmetVolume
    {
        public const float Margin = .06f;

        public static float HeadMargin(Agent agent)
        {
            if (!EnemyPhysicsSettings.HelmetVolume || agent == null || !agent.IsHuman) return 0;
            try { return agent.SpawnEquipment != null && !agent.SpawnEquipment[EquipmentIndex.Head].IsEmpty ? Margin : 0; }
            catch (Exception) { return 0; }
        }

        [HarmonyPatch]
        internal static class FillPatch
        {
            // Compiled once: FieldInfo.GetValue/SetValue boxed every capsule
            // of every body BannerlordVR fills (up to 16 a frame in a battle).
            private delegate void GrowFn(object body, float head, float neck);
            private static GrowFn _grow;
            private static bool _built;

            public static bool Prepare() => EnemyPhysicsSettings.Configured && (AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat") != null);
            public static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat"), "Fill");

            [HarmonyPostfix]
            public static void Postfix(object body, Agent agent)
            {
                if (!EnemyPhysicsSettings.Enabled) return;
                try
                {
                    float margin = HeadMargin(agent);
                    if (margin <= 0 || body == null) return;
                    if (!_built) { _built = true; _grow = Build(body.GetType()); }
                    _grow?.Invoke(body, margin, margin * .5f);
                }
                catch (Exception) { }
            }

            // for (i < min(Count, Capsules.Length)) { c = Capsules[i]; if (c.Part
            // is Head or Neck) { c.Radius += head or neck; Capsules[i] = c; } }
            private static GrowFn Build(Type bodyType)
            {
                try
                {
                    Type capsule = AccessTools.Inner(AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat"), "Capsule");
                    if (capsule == null || AccessTools.Field(bodyType, "Count") == null || AccessTools.Field(bodyType, "Capsules") == null ||
                        AccessTools.Field(capsule, "Radius") == null || AccessTools.Field(capsule, "Part") == null) return null;
                    ParameterExpression raw = Expression.Parameter(typeof(object), "body");
                    ParameterExpression head = Expression.Parameter(typeof(float), "head"), neck = Expression.Parameter(typeof(float), "neck");
                    ParameterExpression caps = Expression.Variable(capsule.MakeArrayType(), "caps"), c = Expression.Variable(capsule, "c");
                    ParameterExpression i = Expression.Variable(typeof(int), "i"), n = Expression.Variable(typeof(int), "n");
                    LabelTarget brk = Expression.Label();
                    Expression body = Expression.Convert(raw, bodyType);
                    Expression part = Expression.Field(c, "Part"), radius = Expression.Field(c, "Radius");
                    BlockExpression block = Expression.Block(new[] { caps, c, i, n },
                        Expression.Assign(caps, Expression.Field(body, "Capsules")),
                        Expression.Assign(n, Expression.Call(typeof(Math).GetMethod("Min", new[] { typeof(int), typeof(int) }),
                            Expression.Field(body, "Count"), Expression.ArrayLength(caps))),
                        Expression.Assign(i, Expression.Constant(0)),
                        Expression.Loop(Expression.IfThenElse(Expression.LessThan(i, n), Expression.Block(
                            Expression.Assign(c, Expression.ArrayIndex(caps, i)),
                            Expression.IfThen(Expression.Equal(part, Expression.Constant(BoneBodyPartType.Head)), Expression.Block(
                                Expression.AddAssign(radius, head), Expression.Assign(Expression.ArrayAccess(caps, i), c))),
                            Expression.IfThen(Expression.Equal(part, Expression.Constant(BoneBodyPartType.Neck)), Expression.Block(
                                Expression.AddAssign(radius, neck), Expression.Assign(Expression.ArrayAccess(caps, i), c))),
                            Expression.PostIncrementAssign(i)), Expression.Break(brk)), brk));
                    return Expression.Lambda<GrowFn>(block, raw, head, neck).Compile();
                }
                catch (Exception ex)
                {
                    PhysicsLog.Error("Compiling the helmet volume failed; head capsules stay bare.", ex);
                    return null;
                }
            }
        }
    }
}
