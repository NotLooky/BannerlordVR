using System;
using System.Collections.Generic;
using System.Globalization;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public enum BladeZone { Edge, Flat, Pommel }

    // Where a held weapon's flat and pommel are. B&S gives each weapon several
    // damagers: the edge (Damager_*Slash) cuts, the point pierces, the handle
    // (Damager_HandleLight/Heavy) strikes blunt, and a slash that lands at a
    // bad angle - the flat - falls back to blunt (badAngleBluntFallback,
    // BluntLight, half damage). The drawn weapon (VrSkinnedWeapon's visual,
    // the meshes VrBlade measures its blade on) is boxed in its own space:
    // across the blade the thinnest side of the box is the flat's normal (a
    // guard or an axe head spreads along the edge, not across it), and what
    // reaches behind the grip is the pommel.
    internal static class BladeShape
    {
        public struct Shape { public bool Valid, HasFlat; public Vec3 LocalFlat; public float Pommel; }
        private delegate bool VisualFn(HandRole role, out MatrixFrame world, out List<MetaMesh> meshes, out int stamp, out string itemId);
        private delegate bool BoundsFn(List<MetaMesh> meshes, out Vec3 min, out Vec3 max);
        private static readonly VisualFn Visual = Bind<VisualFn>(AccessTools.Method(typeof(VrSkinnedWeapon), "TryGetVisual"));
        private static readonly BoundsFn Bounds = Bind<BoundsFn>(AccessTools.Method(typeof(VrBlade), "Bounds"));
        private sealed class Cached { public string Item; public int Stamp; public Shape Shape; }
        private static readonly Cached[] Cache = { new Cached(), new Cached(), new Cached() };
        private static readonly HashSet<string> Logged = new HashSet<string>();
        public const float MaxPommel = .25f;

        private static T Bind<T>(System.Reflection.MethodInfo method) where T : class
        {
            if (method == null) return null;
            try { return Delegate.CreateDelegate(typeof(T), method, false) as T; } catch (Exception) { return null; }
        }

        public static Shape Of(HandRole role, VrBlade.Segment segment)
        {
            if (Visual == null || Bounds == null || !segment.Valid) return default(Shape);
            try
            {
                if (!Visual(role, out _, out List<MetaMesh> meshes, out int stamp, out string item) || meshes == null) return default(Shape);
                Cached c = Cache[Math.Max(0, Math.Min(Cache.Length - 1, (int)role))];
                if (c.Item == item && c.Stamp == stamp) return c.Shape;
                c.Item = item; c.Stamp = stamp; c.Shape = Measure(meshes, segment, item);
                return c.Shape;
            }
            catch (Exception) { return default(Shape); }
        }

        // The handle's share of the weapon from its butt (VrBlade's grip end is
        // the weapon's bottom: 0 m of pommel was ever found behind it).
        // Bannerlord crafts its melee weapons from pieces: the handle and the
        // pommel are the blunt part, the guard is metal on the blade's side.
        // An uncrafted weapon keeps a fifth.
        private static readonly Dictionary<ItemObject, float> Handles = new Dictionary<ItemObject, float>();
        public const float DefaultHandle = .2f, AxeHandle = .75f;
        public static float HandleFraction(ItemObject item)
        {
            if (item == null) return 0;
            if (Handles.TryGetValue(item, out float cached)) return cached;
            float fraction = DefaultHandle;
            try
            {
                WeaponDesign design = item.WeaponDesign;
                if (design?.UsedPieces != null && design.TotalLength > .05f)
                {
                    float handle = 0;
                    foreach (WeaponDesignElement piece in design.UsedPieces)
                    {
                        if (piece == null || !piece.IsValid) continue;
                        CraftingPiece.PieceTypes type = piece.CraftingPiece.PieceType;
                        if (type == CraftingPiece.PieceTypes.Handle || type == CraftingPiece.PieceTypes.Pommel) handle += piece.ScaledLength;
                    }
                    if (handle > 0) fraction = Math.Max(.05f, Math.Min(.9f, handle / design.TotalLength));
                    // An axe's haft runs through its head: counted as handle,
                    // a battle axe was 90% blunt and its head 10 cm of edge.
                    WeaponClass cls = item.PrimaryWeapon?.WeaponClass ?? WeaponClass.Undefined;
                    if (cls == WeaponClass.OneHandedAxe || cls == WeaponClass.TwoHandedAxe || cls == WeaponClass.LowGripPolearm)
                        fraction = Math.Min(fraction, AxeHandle);
                }
            }
            catch (Exception) { }
            Handles[item] = fraction;
            PhysicsLog.Info("BLADE_HANDLE '" + item.StringId + "': handle and pommel " + (fraction * 100).ToString("F0", CultureInfo.InvariantCulture) +
                "% of the weapon from its butt" + (item.WeaponDesign == null ? " (not crafted: default)" : ""));
            return fraction;
        }

        private static Shape Measure(List<MetaMesh> meshes, VrBlade.Segment segment, string item)
        {
            if (!Bounds(meshes, out Vec3 min, out Vec3 max)) return default(Shape);
            Vec3 along = segment.Tip - segment.Grip;
            if (along.Length < .05f) return default(Shape);
            along.Normalize();
            Vec3 size = max - min;
            Vec3[] axes = { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
            float[] extent = { size.x, size.y, size.z };
            int lengthAxis = 0;
            for (int i = 1; i < 3; i++) if (Math.Abs(InteractionMath.Dot(axes[i], along)) > Math.Abs(InteractionMath.Dot(axes[lengthAxis], along))) lengthAxis = i;
            int a = (lengthAxis + 1) % 3, b = (lengthAxis + 2) % 3;
            int thin = extent[a] <= extent[b] ? a : b, wide = thin == a ? b : a;
            float aligned = Math.Abs(InteractionMath.Dot(axes[lengthAxis], along));
            // Behind the grip: the box's corner furthest back along the blade.
            float back = 0;
            for (int k = 0; k < 8; k++)
            {
                var corner = new Vec3((k & 1) == 0 ? min.x : max.x, (k & 2) == 0 ? min.y : max.y, (k & 4) == 0 ? min.z : max.z);
                back = Math.Min(back, InteractionMath.Dot(corner - segment.Grip, along));
            }
            // A box not along the blade, or a weapon as thick as it is wide
            // (a round mace or club): no flat to tell.
            // Half as thick as wide at most: a box 21 x 30 cm round an imperial
            // sword (its guard and grip) told no flat worth trusting.
            bool flat = aligned > .9f && extent[thin] < extent[wide] * .5f;
            var shape = new Shape { Valid = true, HasFlat = flat, LocalFlat = axes[thin], Pommel = Math.Min(MaxPommel, Math.Max(0, -back)) };
            if (Logged.Add(item ?? "?"))
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "BLADE_SHAPE '{0}': box {1:F3} x {2:F3} x {3:F3} m, blade along axis {4} ({5:F2}), flat normal axis {6} ({7:F3} m thick, {8:F3} m wide), pommel {9:F3} m behind the grip{10}",
                    item, size.x, size.y, size.z, lengthAxis, aligned, thin, extent[thin], extent[wide], shape.Pommel, flat ? "" : "; no flat told"));
            return shape;
        }
    }
}
