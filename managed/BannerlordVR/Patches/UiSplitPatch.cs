extern alias NumericsVectors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.GauntletUI.Layout;
using TaleWorlds.TwoDimension;
using Vector2 = NumericsVectors::System.Numerics.Vector2;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 590: each layer's page size. Every GauntletLayer has its own
    /// TwoDimensionEnginePlatform, and its Width/Height ARE the page: the root is
    /// sized from them every frame, the page from them on every layout, and the UI
    /// scale from them (UIContext.UpdateScale, dynamic by default). See VrUiSplit.
    /// The getters are explicit interface implementations, so they are found by name.
    /// </summary>
    [HarmonyPatch]
    public static class UiPageSizePatch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(TwoDimensionEnginePlatform)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(m => m.Name.EndsWith("get_Width", StringComparison.Ordinal) ||
                            m.Name.EndsWith("get_Height", StringComparison.Ordinal));

        private static void Postfix(object __instance, MethodBase __originalMethod, ref float __result)
        {
            if (VrUiSplit.PageFor(__instance, out float w, out float h))
                __result = __originalMethod.Name.EndsWith("get_Width", StringComparison.Ordinal) ? w : h;
        }
    }

    /// <summary>
    /// TEST 591: the clip test in a supersampled interface. The UI and text pixel
    /// shaders discard by SV_Position against ClipRectInfo (two_dimensional_ui.rs
    /// IsClipped) and the circular mask by the same - window pixels both, while
    /// SV_Position is now the dense target's. Scaled for each draw and put back
    /// after, on DrawImage / DrawText: interface methods, so never inlined. The
    /// layout and the mesh positions (NDC from ScreenSize) stay as they are.
    /// </summary>
    [HarmonyPatch]
    public static class UiSupersamplePatch
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            typeof(TwoDimensionEnginePlatform)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(m => m.Name.EndsWith("ITwoDimensionPlatform.DrawImage", StringComparison.Ordinal) ||
                            m.Name.EndsWith("ITwoDimensionPlatform.DrawText", StringComparison.Ordinal));

        private static readonly AccessTools.FieldRef<TwoDimensionEnginePlatform, ScissorTestInfo> Scissor =
            AccessTools.FieldRefAccess<TwoDimensionEnginePlatform, ScissorTestInfo>("_activeScissor");

        private struct Saved
        {
            public bool On;
            public ScissorTestInfo Scissor;
            public bool Circle;
            public Vector2 Center;
            public float Radius, Smoothing;
        }

        private static void Prefix(TwoDimensionEnginePlatform __instance, object[] __args, out Saved __state)
        {
            __state = default;
            float k = VrUiSupersample.ClipScale;
            if (k <= 1f)
                return;

            ref ScissorTestInfo s = ref Scissor(__instance);
            __state.On = true;
            __state.Scissor = s;
            s = new ScissorTestInfo(s.X * k, s.Y * k, s.X2 * k, s.Y2 * k);

            if (__args[0] is SimpleMaterial m && m.CircularMaskingEnabled)
            {
                __state.Circle = true;
                __state.Center = m.CircularMaskingCenter;
                __state.Radius = m.CircularMaskingRadius;
                __state.Smoothing = m.CircularMaskingSmoothingRadius;
                m.CircularMaskingCenter = m.CircularMaskingCenter * k;
                m.CircularMaskingRadius *= k;
                m.CircularMaskingSmoothingRadius *= k;
            }
        }

        private static void Postfix(TwoDimensionEnginePlatform __instance, object[] __args, Saved __state)
        {
            if (!__state.On)
                return;
            Scissor(__instance) = __state.Scissor;
            if (__state.Circle && __args[0] is SimpleMaterial m)
            {
                m.CircularMaskingCenter = __state.Center;
                m.CircularMaskingRadius = __state.Radius;
                m.CircularMaskingSmoothingRadius = __state.Smoothing;
            }
        }
    }

    /// <summary>
    /// TEST 590: each layer's page origin. Vanilla always centres the page in the
    /// window (CalculateCanvas); UpdatePositions then hangs the whole widget tree
    /// from Left/TopUsableAreaStart, and hit tests follow the same rectangles, so
    /// the mouse keeps working wherever the page lands.
    /// </summary>
    [HarmonyPatch(typeof(EventManager), "UpdatePositions")]
    public static class UiPageOriginPatch
    {
        private static readonly Action<EventManager, float> SetLeft =
            AccessTools.MethodDelegate<Action<EventManager, float>>(
                AccessTools.PropertySetter(typeof(EventManager), nameof(EventManager.LeftUsableAreaStart)));

        private static readonly Action<EventManager, float> SetTop =
            AccessTools.MethodDelegate<Action<EventManager, float>>(
                AccessTools.PropertySetter(typeof(EventManager), nameof(EventManager.TopUsableAreaStart)));

        private static void Prefix(EventManager __instance)
        {
            if (!VrUiSplit.OriginFor(__instance, out float x, out float y))
                return;
            SetLeft(__instance, x);
            SetTop(__instance, y);
        }
    }

    /// <summary>
    /// TEST 590: a hand's widget moved into the hand bins. Children lay out in
    /// their parent's own space (DefaultLayout), so placing one widget after its
    /// parent laid it out carries its whole subtree, and its hit rectangles with it.
    /// The place is ABSOLUTE (page pixels), not an offset, so a layout pass that
    /// runs a frame late cannot feed back into the next plan.
    ///
    /// TEST 591: on the PARENT's ILayout.OnLayout, not Widget.Layout. The 590 run
    /// planned six moves and none happened - the laser still landed on the nav bar
    /// at its old place. Widget.Layout is a small non-virtual method the JIT can
    /// inline into its callers, and an inlined copy never reaches a Harmony patch.
    /// OnLayout is called through the ILayout interface, which is never inlined.
    /// </summary>
    [HarmonyPatch]
    public static class UiHandShiftPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type t in typeof(ILayout).Assembly.GetTypes())
            {
                if (t.IsInterface || t.IsAbstract || !typeof(ILayout).IsAssignableFrom(t))
                    continue;
                InterfaceMapping map = t.GetInterfaceMap(typeof(ILayout));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    if (map.InterfaceMethods[i].Name == nameof(ILayout.OnLayout))
                        yield return map.TargetMethods[i];
            }
        }

        private static readonly Action<Widget, float> SetLeft =
            AccessTools.MethodDelegate<Action<Widget, float>>(AccessTools.PropertySetter(typeof(Widget), nameof(Widget.Left)));

        private static readonly Action<Widget, float> SetTop =
            AccessTools.MethodDelegate<Action<Widget, float>>(AccessTools.PropertySetter(typeof(Widget), nameof(Widget.Top)));

        private static void Postfix(object[] __args)
        {
            Dictionary<Widget, Vector2> places = VrUiSplit.Shifts;
            if (places.Count == 0 || !(__args[0] is Widget parent))
                return;
            Vector2 origin = parent.GlobalPosition;
            for (int i = 0; i < parent.ChildCount; i++)
            {
                Widget child = parent.GetChild(i);
                if (child == null || !places.TryGetValue(child, out Vector2 at))
                    continue;
                SetLeft(child, at.X - origin.X);
                SetTop(child, at.Y - origin.Y);
                VrUiSplit.NoteMoved();
            }
        }
    }

    /// <summary>
    /// TEST 645: the pre-battle formation markers sit in the cards' own layer, which
    /// keeps the full window under the split (a world-page layer clipped the cards
    /// moved into the hand bins - "the left hand UI only shows when paused"). Their
    /// positions are remapped onto the world page, and this widget never culls
    /// against its page, so a troop off the panel's edge drew into the HUD and the
    /// hands' part of the window. Hidden there. Land and War Sails use this widget.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.MountAndBlade.GauntletUI.Widgets.Mission.OrderOfBattle.OrderOfBattleFormationMarkerBrushWidget),
                  "OnUpdate")]
    public static class DeployMarkerCullPatch
    {
        private static void Postfix(TaleWorlds.MountAndBlade.GauntletUI.Widgets.Mission.OrderOfBattle.OrderOfBattleFormationMarkerBrushWidget __instance)
        {
            if (!VrUiSplit.Active || !__instance.IsVisible)
                return;
            TaleWorlds.Library.Vec2 p = __instance.Position;
            if (p.x < 0f || p.y < 0f || p.x > VrUiSplit.WorldW || p.y > VrUiSplit.WorldH)
                __instance.IsVisible = false;
        }
    }

    /// <summary>
    /// TEST 647: "the siege machines UI (pre-battle) is not stable and slides with my
    /// head". DeploymentSiegeMachineVM projects its deployment points with the
    /// mission camera's own view-projection matrix - never through MBWindowManager,
    /// so neither the VR panel camera nor the world-page remap reached it. Under the
    /// split it now goes through WorldToScreenInsideUsableArea like the formation
    /// markers (8 m above the point, as vanilla); one off the world page is pushed
    /// off the window, which vanilla's own IsInsideWindow hides.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.MountAndBlade.ViewModelCollection.Order.DeploymentSiegeMachineVM),
                  nameof(TaleWorlds.MountAndBlade.ViewModelCollection.Order.DeploymentSiegeMachineVM.CalculatePosition))]
    public static class DeploySiegeMarkerPatch
    {
        private static bool Prefix(TaleWorlds.MountAndBlade.ViewModelCollection.Order.DeploymentSiegeMachineVM __instance,
                                   ref float ____latestX, ref float ____latestY,
                                   TaleWorlds.Library.Vec3 ____worldPos, TaleWorlds.Engine.Camera ____deploymentCamera)
        {
            if (!VrUiSplit.Active || ____deploymentCamera == null)
                return true;

            TaleWorlds.Library.Vec3 at = ____worldPos;
            at.z += 8f;
            float x = 0f, y = 0f, w = 0f;
            TaleWorlds.MountAndBlade.MBWindowManager.WorldToScreenInsideUsableArea(____deploymentCamera, at,
                                                                                   ref x, ref y, ref w);
            __instance.IsInFront = w > 0f;
            bool onPage = x >= 0f && y >= 0f && x <= VrUiSplit.WorldW && y <= VrUiSplit.WorldH;
            ____latestX = onPage ? x : -10000f;
            ____latestY = onPage ? y : -10000f;
            return false;
        }
    }
}
