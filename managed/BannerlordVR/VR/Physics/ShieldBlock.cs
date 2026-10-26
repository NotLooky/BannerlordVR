using System;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR's VrCollide keeps the hand on the controller and only
    // turns the weapon about the grip (up to collide_max_deg), so a hand
    // pushed into a shield carried the blade through it. In B&S the weapon is
    // a rigidbody held by the hand joint: a shield stops the weapon and the
    // hand with it, and the hand lags the controller. After VrCollide the
    // shown blade is tested against every shield plate near the player (the
    // enemies' and the player's own, for the main hand); one still inside a
    // plate moves the whole hand back out along the plate's normal, to the
    // side the blade was on, by at most MaxLag.
    internal static class ShieldBlock
    {
        // A new mission: the last one's agents and entities are gone.
        internal static void ForgetMission() { PressDepth.Clear(); PressAt.Clear(); Stuck[0] = Stuck[1] = default(StuckBlade); }

        // B&S: an axe, or a blade driven in fast, bites into a wooden shield
        // (DamageModifier_SlashAxe and the pierce rows let it into Wood) and
        // stays in until it is pulled out along itself or wrenched free. The
        // shown weapon and hand stay in the shield and move with it; the
        // controller pulling on it tugs the shield's arm.
        private struct StuckBlade { public bool Active; public int Owner; public MatrixFrame HandLocal; public float Since; }
        private static readonly StuckBlade[] Stuck = new StuckBlade[2];
        private static readonly long[] LastCall = new long[2];
        public const float AxeStick = 5, PointStick = 4, SwordStick = 8, Embed = .04f, PullOut = .12f, WrenchFree = .45f, ThrustCos = .7f;
        public static bool IsStuck(HandRole role) => Stuck[(int)role].Active;

        private static MatrixFrame PlateFrame(VrBlade.Plate plate) =>
            new MatrixFrame(new Mat3(plate.A.NormalizedCopy(), plate.B.NormalizedCopy(), plate.N.NormalizedCopy()), plate.Centre);

        // Held in the shield: the hand is where the bite left it, in the
        // shield's frame; false once the blade is out.
        private static bool HoldStuck(HandRole role, ref MatrixFrame world, Vec3 gripLocal, Vec3 tipLocal, float now)
        {
            StuckBlade s = Stuck[(int)role];
            Agent owner = Mission.Current?.FindAgentWithIndex(s.Owner);
            if (owner == null || !owner.IsActive() || !VrBlade.TryGetEnemyPlate(owner, out VrBlade.Plate plate) || !plate.Valid)
            { Unstick(role, "the shield is gone"); return false; }
            MatrixFrame shown = PlateFrame(plate).TransformToParent(s.HandLocal);
            Vec3 pull = world.origin - shown.origin;
            Vec3 bladeDir = shown.rotation.TransformToParent(tipLocal - gripLocal);
            if (bladeDir.Length < 1e-3f) { Unstick(role, "no blade"); return false; }
            bladeDir.Normalize();
            float outward = -InteractionMath.Dot(pull, bladeDir);
            if (outward > PullOut) { Unstick(role, "pulled out"); return false; }
            if (pull.Length > WrenchFree) { Unstick(role, "wrenched free"); return false; }
            world = shown;
            BlockedAt[(int)role] = System.Diagnostics.Stopwatch.GetTimestamp();
            // The pull on the stuck weapon is a drive on the shield's arm.
            PressDepth[s.Owner] = pull; PressAt[s.Owner] = BlockedAt[(int)role];
            return true;
        }
        private static void Unstick(HandRole role, string why)
        {
            if (!Stuck[(int)role].Active) return;
            PhysicsLog.Info("SHIELD_STUCK blade free of agent " + Stuck[(int)role].Owner + "'s shield: " + why + " after " +
                ((Mission.Current?.CurrentTime ?? 0) - Stuck[(int)role].Since).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s");
            Stuck[(int)role] = default(StuckBlade);
        }
        // What bites into wood: an axe's edge, any point driven along the
        // blade, a sword's edge only when fast.
        private static bool Bites(HandRole role, Vec3 motion, Vec3 bladeDir, float speed)
        {
            Agent player = Mission.Current?.MainAgent;
            if (player == null) return false;
            WeaponComponentData usage = role == HandRole.Main ? player.WieldedWeapon.CurrentUsageItem : player.WieldedOffhandWeapon.CurrentUsageItem;
            if (usage == null || usage.IsShield || !usage.IsMeleeWeapon) return false;
            bool point = motion.Length > 1e-4f && InteractionMath.Dot(motion.NormalizedCopy(), bladeDir) > ThrustCos;
            if (point) return usage.ThrustDamageType == DamageTypes.Pierce && speed >= PointStick;
            if (usage.SwingDamageType != DamageTypes.Cut) return false;
            bool axe = usage.WeaponClass == WeaponClass.OneHandedAxe || usage.WeaponClass == WeaponClass.TwoHandedAxe ||
                usage.WeaponClass == WeaponClass.LowGripPolearm || usage.WeaponClass == WeaponClass.ThrowingAxe;
            return speed >= (axe ? AxeStick : SwordStick);
        }
        public const float MaxLag = 1.2f, Pad = .012f;
        // While a shield holds a blade back, the physics does not strike with
        // it (the controller's blade is behind the shield): per hand role.
        private static readonly long[] BlockedAt = new long[2];
        private static readonly long Fresh = System.Diagnostics.Stopwatch.Frequency / 8;
        public static bool Blocked(HandRole role) =>
            BlockedAt[(int)role] != 0 && System.Diagnostics.Stopwatch.GetTimestamp() - BlockedAt[(int)role] < Fresh;
        private const int Samples = 10;
        // Per enemy agent index: the blade's drive into its shield (world,
        // away from the player), from the last frame the shield held it.
        private static readonly System.Collections.Generic.Dictionary<int, Vec3> PressDepth = new System.Collections.Generic.Dictionary<int, Vec3>();
        private static readonly System.Collections.Generic.Dictionary<int, long> PressAt = new System.Collections.Generic.Dictionary<int, long>();
        public static bool Pressing(int agent, out Vec3 depth)
        {
            depth = Vec3.Zero;
            if (!PressAt.TryGetValue(agent, out long at) || System.Diagnostics.Stopwatch.GetTimestamp() - at > Fresh) return false;
            depth = PressDepth[agent];
            return InteractionMath.IsFinite(depth) && depth.Length > 1e-4f;
        }
        private static bool _broken;
        private static int _logs;
        private static HandRole _role;
        private static float _dt;

        // world: the hand frame VrCollide produced; previous: last shown frame;
        // gripLocal/tipLocal: the blade in the hand frame.
        public static bool Apply(HandRole role, ref MatrixFrame world, MatrixFrame previous, Vec3 gripLocal, Vec3 tipLocal)
        {
            if (_broken || !EnemyPhysicsSettings.SolidShields) return false;
            try
            {
                Agent player = Mission.Current?.MainAgent;
                if (player == null || (tipLocal - gripLocal).Length < .1f) return false;
                long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                float dt = LastCall[(int)role] == 0 ? 0 : (float)((stamp - LastCall[(int)role]) / (double)System.Diagnostics.Stopwatch.Frequency);
                LastCall[(int)role] = stamp;
                float now = Mission.Current.CurrentTime;
                if (Stuck[(int)role].Active && HoldStuck(role, ref world, gripLocal, tipLocal, now)) return true;
                _role = role; _dt = dt > 1e-4f && dt < .1f ? dt : 0;
                Vec3 total = Vec3.Zero;
                bool moved = false;
                Vec3 eye = player.GetEyeGlobalPosition();
                foreach (Agent agent in EnemyPhysicsRuntime.TrackedLiving())
                {
                    if (!VrBlade.TryGetEnemyPlate(agent, out VrBlade.Plate plate) || !plate.Valid) continue;
                    // An enemy's shield is struck from the player's side (as
                    // VrCollide sides it): a fast blade already past its middle
                    // last frame was pushed out the far side.
                    float side = InteractionMath.Dot(eye - plate.Centre, plate.N.NormalizedCopy()) >= 0 ? 1 : -1;
                    moved |= Push(ref world, previous, gripLocal, tipLocal, plate, ref total, agent.Index, side);
                }
                if (role == HandRole.Main && FastAccess.OwnPlate != null)
                {
                    if (FastAccess.OwnPlate(player, out VrBlade.Plate own) && own.Valid)
                        moved |= Push(ref world, previous, gripLocal, tipLocal, own, ref total, -1, 0);
                }
                if (moved) BlockedAt[(int)role] = System.Diagnostics.Stopwatch.GetTimestamp();
                return moved;
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("Solid shields failed; blades go through shields as before.", ex);
                return false;
            }
        }

        private static bool Push(ref MatrixFrame world, MatrixFrame previous, Vec3 gripLocal, Vec3 tipLocal,
            VrBlade.Plate plate, ref Vec3 total, int owner, float side)
        {
            Vec3 n = plate.N.NormalizedCopy();
            if (n.Length < .5f) return false;
            // The own shield: the side the blade was on last frame.
            if (side == 0)
            {
                Vec3 before = previous.TransformToParent((gripLocal + tipLocal) * .5f);
                side = InteractionMath.Dot(before - plate.Centre, n) >= 0 ? 1 : -1;
            }
            // Held only when the blade comes in through the shield's face: a
            // blade point crosses the front face, inside its outline, between
            // last frame and this one. A blade that went round the edge (or
            // was already behind) is never dragged back through: that was the
            // sword "teleported to the other side".
            bool entered = false;
            Vec3 entryMotion = Vec3.Zero;
            for (int i = 0; i <= Samples && !entered; i++)
            {
                Vec3 local = gripLocal + (tipLocal - gripLocal) * (i / (float)Samples);
                Vec3 p0 = previous.TransformToParent(local) - plate.Centre, p1 = world.TransformToParent(local) - plate.Centre;
                entryMotion = p1 - p0;
                float d0 = side * InteractionMath.Dot(p0, n), d1 = side * InteractionMath.Dot(p1, n);
                if (d0 < plate.HalfN || d1 >= plate.HalfN + Pad) continue;
                float t = Math.Abs(d0 - d1) < 1e-5f ? 0 : (d0 - plate.HalfN) / (d0 - d1);
                Vec3 hit = p0 + (p1 - p0) * Math.Max(0, Math.Min(1, t));
                entered = Math.Abs(InteractionMath.Dot(hit, plate.A)) <= plate.HalfA + Pad && Math.Abs(InteractionMath.Dot(hit, plate.B)) <= plate.HalfB + Pad;
            }
            if (!entered) return false;
            float need = 0;
            for (int i = 0; i <= Samples; i++)
            {
                Vec3 p = world.TransformToParent(gripLocal + (tipLocal - gripLocal) * (i / (float)Samples));
                Vec3 d = p - plate.Centre;
                if (Math.Abs(InteractionMath.Dot(d, plate.A)) > plate.HalfA + Pad || Math.Abs(InteractionMath.Dot(d, plate.B)) > plate.HalfB + Pad) continue;
                float depth = plate.HalfN + Pad - side * InteractionMath.Dot(d, n);
                if (depth > 0 && depth < 2 * plate.HalfN + .5f) need = Math.Max(need, depth);
            }
            if (need <= 0) return false;
            // A biting blow stays in, Embed deep past the face.
            Vec3 bladeWorld = world.rotation.TransformToParent(tipLocal - gripLocal);
            bool bites = owner >= 0 && _dt > 0 && bladeWorld.Length > 1e-3f && !Stuck[(int)_role].Active &&
                Bites(_role, entryMotion, bladeWorld.NormalizedCopy(), entryMotion.Length / _dt);
            if (bites) need = Math.Max(0, need - Embed);
            Vec3 shift = n * (side * need);
            if ((total + shift).Length > MaxLag) return false; // past the hand's reach: it goes through
            total += shift;
            world.origin += shift;
            // How far the controller drives the blade into this shield: the
            // B&S weapon pushes the shield (and the arm holding it) with that.
            if (owner >= 0) { PressDepth[owner] = -shift; PressAt[owner] = System.Diagnostics.Stopwatch.GetTimestamp(); }
            if (bites)
            {
                Stuck[(int)_role] = new StuckBlade { Active = true, Owner = owner, HandLocal = PlateFrame(plate).TransformToLocal(world),
                    Since = Mission.Current?.CurrentTime ?? 0 };
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "SHIELD_STUCK blade bit into agent {0}'s shield at {1:F1} m/s; pull it out along the blade or wrench it free",
                    owner, entryMotion.Length / _dt));
                PhysicsInterop.Haptic(_role == HandRole.Main ? 1 : 0, .7f, .06f);
            }
            if (_logs < 6)
            {
                _logs++;
                PhysicsLog.Info("SHIELD blade stopped by " + (owner < 0 ? "the player's shield" : "agent " + owner + "'s shield") +
                    " (hand held back " + need.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " m, side " + side +
                    ", blade middle " + InteractionMath.Dot(world.TransformToParent((gripLocal + tipLocal) * .5f) - plate.Centre, n).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    " m off the plate centre along its normal)");
            }
            return true;
        }
    }
}
