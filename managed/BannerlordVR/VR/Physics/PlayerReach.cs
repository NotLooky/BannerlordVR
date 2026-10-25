using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR walks the player's agent under the headset by teleporting
    // it each tick (VrRoomscale.Step). Stepped into an enemy's capsule, the
    // engine pushes the agent back out and the view goes with it; past
    // roomscale_recentre_m (0.55 m) of lead the view is pulled back onto the
    // body. Next to an enemy the body therefore stops at the capsules'
    // contact while the head and hands lean on into reach, as a B&S player
    // leans past its locomotion capsule.
    internal static class PlayerReach
    {
        // Gap at which the engine last stopped the player against an enemy:
        // its agent separation keeps more room than the body capsules.
        private static float _stopGap;
        private static float Near => Math.Max(.5f, _stopGap + .3f);

        // Lead the head may take over the body near an enemy.
        public static float Recentre(float configured)
        {
            float reach = EnemyPhysicsSettings.CloseReach;
            if (reach <= configured || !EnemyPhysicsSettings.Enabled) return configured;
            Agent player = Mission.Current?.MainAgent;
            if (player == null || !player.IsActive() || player.HasMount) return configured;
            return EnemyPhysicsRuntime.Clearance(player.Position.AsVec2, Radius(player)) < Near ? reach : configured;
        }

        // Stick locomotion is stopped by the engine short of the enemy; the
        // stick then walks the view on into reach (at walking speed, up to
        // CloseReach), the body staying where the engine stopped it. Blocked
        // means pushing the stick towards a close enemy while barely moving.
        private const float LeanSpeed = 1.5f, BlockRange = .8f, BlockedSpeed = .35f;
        private static bool _loggedLean;
        private static float _nextTrace;
        public static void StickLean(Agent player, float dt)
        {
            try
            {
                float reach = EnemyPhysicsSettings.CloseReach;
                // A rider's stick drives the horse, not a lean into reach.
                if (reach <= 0 || player == null || !player.IsActive() || player.HasMount || dt <= 0 || dt > .1f) return;
                Vec2 input = player.MovementInputVector;
                Vec2 at = player.Position.AsVec2;
                float gap = EnemyPhysicsRuntime.Nearest(at, Radius(player), out Vec2 enemy);
                Vec3 velocity = player.Velocity;
                float speed = new Vec2(velocity.x, velocity.y).Length;
                if (EnemyPhysicsSettings.Trace && gap < 1.5f && input.LengthSquared >= .04f && Mission.Current.CurrentTime >= _nextTrace)
                {
                    _nextTrace = Mission.Current.CurrentTime + 1;
                    PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "CLOSE_REACH stick input=({0:F2},{1:F2}) gap={2:F2} speed={3:F2} lead={4:F2}",
                        input.x, input.y, gap, speed, new Vec2(VrCameraDriver.RoomOffset.x, VrCameraDriver.RoomOffset.y).Length));
                }
                if (input.LengthSquared < .04f || gap > BlockRange) return;
                Vec3 look = player.LookDirection;
                Vec2 forward = new Vec2(look.x, look.y);
                Vec2 toEnemy = enemy - at;
                if (forward.Length < 1e-3f || toEnemy.Length < 1e-3f) return;
                forward = forward.Normalized(); toEnemy = toEnemy.Normalized();
                Vec2 right = new Vec2(forward.y, -forward.x);
                Vec2 move = right * input.x + forward * input.y;
                float amount = Math.Min(1, move.Length);
                if (amount < 1e-3f || Vec2.DotProduct(move.Normalized(), toEnemy) < .3f) return;
                // Blocked: the speed towards the enemy, not the total speed.
                // At full stick the engine slides the player round the enemy
                // at walking speed, which never counted as stopped.
                if (Vec2.DotProduct(new Vec2(velocity.x, velocity.y), toEnemy) > BlockedSpeed) return;
                _stopGap = Math.Max(0, gap);
                if (!_loggedLean)
                {
                    _loggedLean = true;
                    PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "CLOSE_REACH stick blocked at capsule gap {0:F2} m; the view leans on", gap));
                }
                Vec3 offset = VrCameraDriver.RoomOffset;
                float lead = new Vec2(offset.x, offset.y).Length;
                if (lead >= reach) return;
                Vec2 dir = move.Normalized();
                float step = Math.Min(reach - lead, LeanSpeed * amount * dt);
                Vec3 local = VrCameraDriver.RoomRotation.TransformToLocal(new Vec3(dir.x * step, dir.y * step, 0));
                local.z = 0;
                VrCameraDriver.ConsumeRoomOffset(-local);
            }
            catch (Exception ex)
            {
                PhysicsLog.Error("Stick lean failed.", ex);
            }
        }

        // The stick reaches the agent in MissionMainAgentController.ControlTick
        // (BannerlordVR's MovementInputPatch turns it head-relative there);
        // read in the mission tick it was always empty.
        [HarmonyPatch]
        internal static class ControlTickPatch
        {
            private static float _last = -1;
            // Missing, the lean is off and every other patch still applies.
            public static bool Prepare() => EnemyPhysicsSettings.Configured && (TargetMethod() != null);
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("TaleWorlds.MountAndBlade.View.MissionViews.MissionMainAgentController"), "ControlTick", new Type[0]);

            [HarmonyPostfix, HarmonyPriority(Priority.Last)]
            public static void Postfix()
            {
                Mission mission = Mission.Current;
                if (mission == null) return;
                float now = mission.CurrentTime, dt = _last < 0 ? 0 : now - _last;
                _last = now;
                if (dt > 0 && EnemyPhysicsSettings.Enabled) StickLean(mission.MainAgent, dt);
            }
        }

        public static float Radius(Agent agent) =>
            agent?.Monster != null && agent.Monster.BodyCapsuleRadius > 0 ? agent.Monster.BodyCapsuleRadius * agent.AgentScale : .37f;

        [HarmonyPatch]
        internal static class StepPatch
        {
            // Off unless enemy_physics: without it the game plays as without this patch.
            public static bool Prepare() => EnemyPhysicsSettings.Configured;
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrRoomscale"), "Step");

            public struct Before { public bool Valid; public Vec3 Position; public Vec3 Offset; }

            [HarmonyPrefix]
            public static void Prefix(Agent agent, out Before __state)
            {
                __state = default(Before);
                // A rider is never put back: the horse carries it.
                if (agent == null || agent.HasMount || EnemyPhysicsSettings.CloseReach <= 0 || !EnemyPhysicsSettings.Enabled) return;
                __state = new Before { Valid = true, Position = agent.Position, Offset = VrCameraDriver.RoomOffset };
            }

            [HarmonyPostfix]
            public static void Postfix(Agent agent, Before __state)
            {
                try
                {
                    if (!__state.Valid || agent == null || !agent.IsActive()) return;
                    Vec3 after = agent.Position;
                    if ((after - __state.Position).AsVec2.LengthSquared < 1e-8f) return;
                    float radius = Radius(agent);
                    float clearance = EnemyPhysicsRuntime.Clearance(after.AsVec2, radius);
                    if (clearance >= _stopGap || clearance >= EnemyPhysicsRuntime.Clearance(__state.Position.AsVec2, radius)) return;
                    // Into an enemy: the body stays at the contact, the
                    // headset keeps the lead it would have consumed.
                    agent.TeleportToPosition(__state.Position);
                    Vec3 offset = VrCameraDriver.RoomOffset;
                    VrCameraDriver.ConsumeRoomOffset(offset - __state.Offset);
                }
                catch (Exception ex)
                {
                    PhysicsLog.Error("Close reach step failed; roomscale walks as BannerlordVR does.", ex);
                }
            }
        }

        // VrRoomscale.Tick reads RecentreAt once, for the recentre pull.
        [HarmonyPatch]
        internal static class RecentrePatch
        {
            // Off unless enemy_physics: without it the game plays as without this patch.
            public static bool Prepare() => EnemyPhysicsSettings.Configured;
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrRoomscale"), "Tick");

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo field = AccessTools.Field(AccessTools.TypeByName("BannerlordVR.VR.VrRoomscale"), "RecentreAt");
                MethodInfo hook = AccessTools.Method(typeof(PlayerReach), nameof(Recentre));
                int replaced = 0;
                foreach (CodeInstruction instruction in instructions)
                {
                    yield return instruction;
                    if (field != null && instruction.LoadsField(field)) { replaced++; yield return new CodeInstruction(OpCodes.Call, hook); }
                }
                if (replaced == 0) PhysicsLog.Info("Close reach: VrRoomscale.Tick has no RecentreAt read; the view recentres at its configured lead.");
            }
        }
    }
}
