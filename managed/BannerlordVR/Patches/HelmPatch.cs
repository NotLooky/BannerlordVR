using System;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.Patches
{
    /// <summary>
    /// TEST 473: the War Sails helm seam. MissionShipControlView.HandleShipControls
    /// builds the player's ShipInputRecord from the raw movement axes and hands it
    /// to PlayerShipController.SetInput; this postfix replaces only its rudder
    /// while VrHelm's hand holds the tiller. Rowers and sail stay the stick's.
    ///
    /// NavalDLC is optional and is not referenced at build time, so everything
    /// goes through reflection, and the patch is laid down at the main menu
    /// (SubModule) - by then every module's assemblies are loaded, which is not
    /// promised at OnSubModuleLoad for a module this one does not depend on.
    /// </summary>
    internal static class HelmPatch
    {
        private static bool _installed;
        private static PropertyInfo _viewMachine;
        private static PropertyInfo _machineShip;
        private static PropertyInfo _machineHandle;
        private static PropertyInfo _shipControlled;
        private static PropertyInfo _shipPlayer;
        private static PropertyInfo _shipVisualRudder;
        private static MethodInfo _update;
        private static MethodInfo _setInput;
        private static MethodInfo _setRudder;

        private static object _view;
        private static Mission _viewMission;

        internal static void Install(Harmony harmony)
        {
            if (_installed || harmony == null)
                return;

            _installed = true;
            try
            {
                Type view = AccessTools.TypeByName("NavalDLC.View.MissionViews.MissionShipControlView");
                if (view == null)
                {
                    VrLog.Info("TEST 473: no War Sails (NavalDLC.View) loaded - the helm stays vanilla.");
                    return;
                }

                Type machine = AccessTools.TypeByName("NavalDLC.Missions.Objects.UsableMachines.ShipControllerMachine");
                Type ship = AccessTools.TypeByName("NavalDLC.Missions.Objects.MissionShip");
                Type controller = AccessTools.TypeByName("NavalDLC.Missions.ShipControl.PlayerShipController");
                Type record = AccessTools.TypeByName("NavalDLC.Missions.ShipInput.ShipInputRecord");
                MethodInfo target = AccessTools.Method(view, "HandleShipControls", new[] { typeof(float) });

                _viewMachine = AccessTools.Property(view, "ControllerMachine");
                _machineShip = machine == null ? null : AccessTools.Property(machine, "AttachedShip");
                _machineHandle = machine == null ? null : AccessTools.Property(machine, "HandTargetEntity");
                _shipControlled = ship == null ? null : AccessTools.Property(ship, "IsPlayerControlled");
                _shipPlayer = ship == null ? null : AccessTools.Property(ship, "PlayerController");
                _shipVisualRudder = ship == null ? null : AccessTools.Property(ship, "VisualRudderRotation");
                _update = controller == null ? null : AccessTools.Method(controller, "Update", new[] { typeof(float) });
                _setInput = controller == null || record == null ? null
                    : AccessTools.Method(controller, "SetInput", new[] { record.MakeByRefType() });
                _setRudder = record == null ? null : AccessTools.Method(record, "SetRudderLateral", new[] { typeof(float) });

                if (target == null || _viewMachine == null || _machineShip == null || _machineHandle == null
                    || _shipControlled == null || _shipPlayer == null || _update == null || _setInput == null
                    || _setRudder == null)
                {
                    VrLog.Warn("TEST 473: War Sails' helm is not where it was (a member is missing) - the helm "
                               + "stays vanilla.");
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(typeof(HelmPatch), nameof(Postfix)));
                VrLog.Info("TEST 473: the helm can be steered by hand (grip the tiller while at it).");
            }
            catch (Exception ex)
            {
                VrLog.Error("TEST 473: the helm patch failed; the helm stays vanilla.", ex);
            }
        }

        private static void Postfix(object __instance)
        {
            _view = __instance;
            _viewMission = Mission.Current;
            if (!VrHelm.Held)
                return;

            try
            {
                object ship = Ship(Machine);
                if (ship == null || !(bool)_shipControlled.GetValue(ship))
                    return;

                object controller = _shipPlayer.GetValue(ship);
                if (controller == null)
                    return;

                // The record is a struct: the boxed copy is changed in place and
                // the same box goes back in.
                object record = _update.Invoke(controller, new object[] { 0f });
                _setRudder.Invoke(record, new object[] { VrHelm.RudderFor(VrHelm.Fraction) });
                _setInput.Invoke(controller, new[] { record });
            }
            catch (Exception ex)
            {
                VrHelm.Fail(ex);
            }
        }

        /// <summary>The ship controller machine the player uses, or null.</summary>
        internal static object Machine =>
            _view != null && ReferenceEquals(_viewMission, Mission.Current) ? _viewMachine.GetValue(_view) : null;

        internal static GameEntity Handle(object machine) =>
            machine == null ? null : _machineHandle.GetValue(machine) as GameEntity;

        internal static object Ship(object machine) => machine == null ? null : _machineShip.GetValue(machine);

        internal static float VisualRudder(object ship) =>
            ship != null && _shipVisualRudder != null ? (float)_shipVisualRudder.GetValue(ship) : 0f;
    }
}
