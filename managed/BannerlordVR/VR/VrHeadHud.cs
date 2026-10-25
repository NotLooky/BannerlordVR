using System;
using System.Collections.Generic;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 377g: what stays on the panel in front of the face during a battle.
    ///
    /// User: "i want formation ui and weapon wheel and notification in head" -
    /// and nothing else; the health, kill feed and army strength are on the wrists
    /// (VrWristHud). So while no menu is open, every interface layer that is not
    /// one of those is faded out with UIContext.ContextAlpha (the crosshair's and
    /// photo mode's way), and inside MainAgentHUD - which must still be DRAWN,
    /// because the left wrist is cropped from it - the interaction prompts
    /// (AgentFocus) and the hit-direction indicator (AgentTakenDamage) are made
    /// invisible. Both are prefab roots with no visibility binding of their own,
    /// so nothing in vanilla turns them back on.
    ///
    /// A menu open over the battle (escape, conversation, barter, any focus layer)
    /// lifts all of it, so nothing a menu needs can go missing. Every layer hidden
    /// is named in the log once.
    /// </summary>
    internal static class VrHeadHud
    {
        private static readonly bool Enabled = VrConfig.Bool("head_hud_filter", true);

        /// <summary>TEST 462: the VR panel's "Pickup prompt" - AgentFocus (the item's
        /// name and its button) is left up.</summary>
        internal static bool Prompts = VrConfig.Bool("hud_prompts", false);

        private static readonly HashSet<string> Keep = new HashSet<string>
        {
            // Formation UI.
            "MissionFormationMarker", "MissionOrder", "MissionOrderOfBattle", "MissionSiegeEngineMarker",
            // TEST 631: the town/village NPC and shop names (left bumper = ShowIndicators),
            // on the world page with the formation markers.
            "MissionNameMarker",
            // War Sails' own deployment screen (the left palette's source) and
            // its ship markers.
            "NavalOrderOfBattle", "NavalShipMarker",
            // The wheels.
            "MissionEquipmentController", "MissionEquipDrop", "MissionCheerController",
            // Notifications.
            "ChatLog", "GameNotification", "SceneNotification", "FullScreenNotice",
            "BoundaryCrossing", "MissionLeave",
            // The wrists' sources: drawn, and cropped off this panel by native.
            "MainAgentHUD", "Scoreboard", "MissionSPKillFeed",
            // The game's own machinery.
            "QueryManager", "CameraFade", "LoadingWindow", "Tooltip", "GamepadCusor",
            "TutorialScreen", "MissionEscapeMenu", "MissionOptions", "MissionPhotoMode",
        };

        private static readonly HashSet<GauntletLayer> Faded = new HashSet<GauntletLayer>();
        private static readonly HashSet<string> Logged = new HashSet<string>();
        private static readonly List<GauntletLayer> Gone = new List<GauntletLayer>();

        private static GauntletLayer _hudLayer;
        private static Widget _focus;

        /// <summary>TEST 636: MainAgentHUD's AgentFocus ("talk", "pick up"), or null.</summary>
        internal static Widget Focus => _focus;
        private static Widget _taken;
        private static bool _broken;

        public static void Tick()
        {
            if (!Enabled || _broken)
                return;

            try
            {
                bool filter = Mission.Current != null && !VrMissionPhase.UiMenuOpen;
                if (!filter)
                {
                    RestoreAll();
                    return;
                }

                List<ScreenLayer> layers = ScreenManager.SortedLayers;
                for (int i = 0; i < layers.Count; i++)
                {
                    // The crosshair is CrosshairPatch's (TEST 376), menus or not.
                    if (!(layers[i] is GauntletLayer g) || g.IsFinalized || g.UIContext == null ||
                        g.Name == "MissionCrosshair")
                        continue;

                    if (Keep.Contains(g.Name) || g.IsFocusLayer)
                    {
                        if (Faded.Remove(g))
                            g.UIContext.ContextAlpha = 1f;
                        if (g.Name == "MainAgentHUD")
                            HidePromptsIn(g);
                        continue;
                    }

                    g.UIContext.ContextAlpha = 0f;
                    if (Faded.Add(g) && Logged.Add(g.Name))
                        VrLog.Info("Head HUD (TEST 377g): \"" + g.Name + "\" hidden in battle "
                                   + "(only formations, wheels and notifications stay in front of "
                                   + "the face; head_hud_filter = 0 shows everything).");
                }

                Gone.Clear();
                foreach (GauntletLayer g in Faded)
                {
                    if (g.IsFinalized)
                        Gone.Add(g);
                }
                foreach (GauntletLayer g in Gone)
                    Faded.Remove(g);
            }
            catch (Exception ex)
            {
                _broken = true;
                RestoreAll();
                VrLog.Error("Head HUD filter failed; the whole HUD is back on the head panel.", ex);
            }
        }

        /// <summary>The prompts and the hit indicator, out of MainAgentHUD. Looked
        /// up once per layer: the prefabs compile into generated classes
        /// ("..._AgentFocus__DependendPrefab"), so they are matched by name.</summary>
        private static void HidePromptsIn(GauntletLayer layer)
        {
            if (!ReferenceEquals(layer, _hudLayer))
            {
                _hudLayer = layer;
                Widget root = layer.UIContext.Root;
                _focus = First(root, "_AgentFocus__");
                _taken = First(root, "_AgentTakenDamage__");
            }

            if (_focus != null)
                _focus.IsVisible = Prompts;
            if (_taken != null)
                _taken.IsVisible = false;
        }

        private static void RestoreAll()
        {
            foreach (GauntletLayer g in Faded)
            {
                if (!g.IsFinalized && g.UIContext != null)
                    g.UIContext.ContextAlpha = 1f;
            }
            Faded.Clear();

            if (_hudLayer != null && !_hudLayer.IsFinalized)
            {
                if (_focus != null)
                    _focus.IsVisible = true;
                if (_taken != null)
                    _taken.IsVisible = true;
            }
            _hudLayer = null;
            _focus = null;
            _taken = null;
        }

        private static Widget First(Widget w, string typeNamePart)
        {
            if (w == null)
                return null;
            if (w.GetType().Name.Contains(typeNamePart))
                return w;
            for (int i = 0; i < w.ChildCount; i++)
            {
                Widget hit = First(w.GetChild(i), typeNamePart);
                if (hit != null)
                    return hit;
            }
            return null;
        }
    }
}
