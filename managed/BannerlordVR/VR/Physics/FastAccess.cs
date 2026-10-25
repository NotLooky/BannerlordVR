using System;
using System.Linq.Expressions;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR internals read every frame, bound once to typed delegates.
    // MethodInfo.Invoke and FieldInfo.GetValue allocate an argument array and
    // box every value read: several thousand small objects a frame from the
    // body capsules alone, each a step towards a garbage collection pause.
    internal static class FastAccess
    {
        public delegate bool OwnPlateFn(Agent agent, out VrBlade.Plate plate);
        public static readonly OwnPlateFn OwnPlate = Bind<OwnPlateFn>(AccessTools.Method(typeof(VrBlade), "TryGetOwnPlate"));

        // VrPhysicalCombat.BodyOf(agent) copied into flat arrays: the count of
        // capsules, or -1 when it has no body or the body is another agent's
        // (BodyOf reuses a ring of 16 within a tick).
        public delegate int CapsulesFn(Agent agent, Vec3[] a, Vec3[] b, float[] radius, sbyte[] bone);
        public static readonly CapsulesFn Capsules = BuildCapsules();

        // VrDrawnBow.VertexCount: the vertices a ManagedMeshEditOperations holds
        // (read from its native buffer; the engine exposes no count).
        public static readonly Func<ManagedMeshEditOperations, int> VertexCount =
            Bind<Func<ManagedMeshEditOperations, int>>(AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrDrawnBow"), "VertexCount"));

        // VrTwoHand.Active and VrTwoHand.Gripping (the hand on the shaft or
        // the blade besides the main one).
        private static readonly Type TwoHand = AccessTools.TypeByName("BannerlordVR.VR.VrTwoHand");
        public static readonly Func<bool> TwoHandActive = Bind<Func<bool>>(AccessTools.PropertyGetter(TwoHand, "Active"));
        public static readonly Func<VrHand?> TwoHandGripping = Bind<Func<VrHand?>>(AccessTools.PropertyGetter(TwoHand, "Gripping"));

        static FastAccess()
        {
            PhysicsLog.Info("FAST_ACCESS own_plate=" + (OwnPlate != null ? "bound" : "MISSING") +
                " capsules=" + (Capsules != null ? "compiled" : "MISSING") +
                " two_hand=" + (TwoHandActive != null && TwoHandGripping != null ? "bound" : "MISSING"));
        }

        private static T Bind<T>(MethodInfo method) where T : class
        {
            if (method == null) return null;
            try { return Delegate.CreateDelegate(typeof(T), method, false) as T; }
            catch (Exception ex) { PhysicsLog.Error("Binding " + method.Name + " failed.", ex); return null; }
        }

        private static CapsulesFn BuildCapsules()
        {
            try
            {
                Type combat = AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat");
                Type body = AccessTools.Inner(combat, "Body"), capsule = AccessTools.Inner(combat, "Capsule");
                MethodInfo bodyOf = AccessTools.Method(combat, "BodyOf", new[] { typeof(Agent) });
                if (body == null || capsule == null || bodyOf == null) return null;
                ParameterExpression agent = Expression.Parameter(typeof(Agent), "agent");
                ParameterExpression a = Expression.Parameter(typeof(Vec3[]), "a"), b = Expression.Parameter(typeof(Vec3[]), "b");
                ParameterExpression r = Expression.Parameter(typeof(float[]), "radius"), bone = Expression.Parameter(typeof(sbyte[]), "bone");
                ParameterExpression found = Expression.Variable(body, "found"), caps = Expression.Variable(capsule.MakeArrayType(), "caps");
                ParameterExpression i = Expression.Variable(typeof(int), "i"), n = Expression.Variable(typeof(int), "n");
                LabelTarget done = Expression.Label(typeof(int)), brk = Expression.Label();
                Expression item = Expression.ArrayIndex(caps, i);
                BlockExpression block = Expression.Block(new[] { found, caps, i, n },
                    Expression.Assign(found, Expression.Call(bodyOf, agent)),
                    Expression.IfThen(Expression.OrElse(Expression.Equal(found, Expression.Constant(null, body)),
                        Expression.NotEqual(Expression.Field(found, "Agent"), Expression.Property(agent, "Index"))),
                        Expression.Return(done, Expression.Constant(-1))),
                    Expression.Assign(caps, Expression.Field(found, "Capsules")),
                    Expression.Assign(n, Expression.Call(typeof(Math).GetMethod("Min", new[] { typeof(int), typeof(int) }),
                        Expression.Field(found, "Count"), Expression.Call(typeof(Math).GetMethod("Min", new[] { typeof(int), typeof(int) }),
                            Expression.ArrayLength(a), Expression.ArrayLength(caps)))),
                    Expression.Assign(i, Expression.Constant(0)),
                    Expression.Loop(Expression.IfThenElse(Expression.LessThan(i, n), Expression.Block(
                        Expression.Assign(Expression.ArrayAccess(a, i), Expression.Field(item, "A")),
                        Expression.Assign(Expression.ArrayAccess(b, i), Expression.Field(item, "B")),
                        Expression.Assign(Expression.ArrayAccess(r, i), Expression.Field(item, "Radius")),
                        Expression.Assign(Expression.ArrayAccess(bone, i), Expression.Field(item, "Bone")),
                        Expression.PostIncrementAssign(i)), Expression.Break(brk)), brk),
                    Expression.Label(done, n));
                return Expression.Lambda<CapsulesFn>(block, agent, a, b, r, bone).Compile();
            }
            catch (Exception ex)
            {
                PhysicsLog.Error("Compiling the capsule reader failed; hits use the engine limbs.", ex);
                return null;
            }
        }
    }
}
