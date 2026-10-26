using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace BannerlordVR.VR.Physics
{
    internal static class JitWarmup
    {
        public static void Prepare(params Type[] types)
        {
            int prepared = 0;
            foreach (Type type in types)
            {
                foreach (Type t in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
                {
                    if (t.ContainsGenericParameters) continue;
                    foreach (MethodInfo method in t.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (method.IsAbstract || method.ContainsGenericParameters) continue;
                        try { RuntimeHelpers.PrepareMethod(method.MethodHandle); prepared++; }
                        catch { }
                    }
                }
            }
            PhysicsLog.Info("Physics JIT warm-up prepared " + prepared + " methods.");
        }

        private static System.Collections.Generic.IEnumerable<Type> Concat(this Type[] first, Type[] second)
        {
            foreach (Type t in first) yield return t;
            foreach (Type t in second) yield return t;
        }
    }
}
