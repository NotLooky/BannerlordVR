using System;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using SandBox.View.Map;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// MapScreen's IMapStateHandler.BeforeTick: the map camera is rebuilt inside
    /// it (MapCameraView.OnBeforeTick, then SetCameraOfSceneLayer, which is too
    /// small to patch safely - it may be inlined). The postfix is the last word on
    /// the camera before the map renders. See VrMapStereo.
    /// </summary>
    [HarmonyPatch]
    public static class MapScreenBeforeTickPatch
    {
        private static MethodBase TargetMethod()
        {
            InterfaceMapping map = typeof(MapScreen).GetInterfaceMap(typeof(IMapStateHandler));
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == nameof(IMapStateHandler.BeforeTick))
                    return map.TargetMethods[i];
            }

            throw new MissingMethodException("MapScreen", "IMapStateHandler.BeforeTick");
        }

        private static void Postfix(MapScreen __instance)
        {
            VrMapStereo.AfterCameraUpdate(__instance);
        }
    }

    /// <summary>The left stick pans the map table the way the player faces (see
    /// VrMapStereo.SteerPan), and the table no longer glides to a settlement on
    /// entering it (TEST 526, VrMapStereo.HoldAtSettlement).</summary>
    [HarmonyPatch(typeof(MapCameraView), nameof(MapCameraView.OnBeforeTick))]
    public static class MapStickPanPatch
    {
        private static void Prefix(MapCameraView __instance, ref MapCameraView.InputInformation inputInformation)
        {
            VrMapStereo.LaserOwnsCursor();
            VrMapStereo.HoldAtSettlement(__instance, ref inputInformation);
            VrMapStereo.SteerPan(ref inputInformation);
        }
    }

    /// <summary>TEST 526: a nameplate click no longer flies and zooms the table.
    /// See VrMapStereo.AllowFastMove.</summary>
    [HarmonyPatch(typeof(MapCameraView), nameof(MapCameraView.FastMoveCameraToPosition))]
    public static class MapFastMovePatch
    {
        private static bool Prefix() => VrMapStereo.AllowFastMove();
    }

    /// <summary>
    /// The ray behind every map click, hover and ground cursor, including the
    /// gamepad cursor's, which casts from the screen centre through the camera
    /// we are swinging between the eyes. Replaced with the laser.
    /// </summary>
    [HarmonyPatch(typeof(MapScreen), nameof(MapScreen.GetCursorIntersectionPoint))]
    public static class MapCursorRayPatch
    {
        private static void Prefix(ref Vec3 clippedMouseNear, ref Vec3 clippedMouseFar)
        {
            float length = (clippedMouseFar - clippedMouseNear).Length;
            if (VrMapStereo.TryLaser(null, length > 1f ? length : 4000f, out Vec3 near, out Vec3 far))
            {
                clippedMouseNear = near;
                clippedMouseFar = far;
            }
        }
    }

    /// <summary>
    /// The raw mouse ray HandleMouse also hands to the party and settlement
    /// visuals for hover and selection, so they agree with the ground hit above.
    /// </summary>
    [HarmonyPatch(typeof(SceneView), nameof(SceneView.TranslateMouse))]
    public static class MapTranslateMousePatch
    {
        private static void Postfix(SceneView __instance, ref Vec3 worldMouseNear,
                                    ref Vec3 worldMouseFar)
        {
            float length = (worldMouseFar - worldMouseNear).Length;
            if (VrMapStereo.TryLaser(__instance, length > 1f ? length : 4000f,
                                     out Vec3 near, out Vec3 far))
            {
                worldMouseNear = near;
                worldMouseFar = far;
            }
        }
    }
}
