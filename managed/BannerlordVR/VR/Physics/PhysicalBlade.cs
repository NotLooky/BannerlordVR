using System;
using System.Diagnostics;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery's weapon is a rigidbody held by the player's hand
    // joint: once it is jointed into a part (Damager.Penetrate) the weapon
    // and the virtual hand stay with the part while the controller moves on,
    // and the arm joint pulls the part instead. BannerlordVR draws the hand
    // (arm IK) and the weapon on the hand target that VrCollide.Resolve
    // returns. While a blade is in a body that target is replaced by the
    // controller pose constrained by the penetration joint:
    //  - angular X/Y limited: the blade keeps its direction in the part,
    //    within the shallow-depth limit, turning about the entry point;
    //  - x/y locked: the blade line passes through the entry point;
    //  - z limited: the blade goes no deeper than the joint's limit.
    // The physics keeps working from the raw controller blade, so the part is
    // pulled by the controller as by the B&S arm joint.
    internal static class PhysicalBlade
    {
        private sealed class State
        {
            public MatrixFrame Raw, Shown;
            public long At;
            public bool Pierced;
            public Vec3 Anchor, PartAxis, GripLocal, TipLocal;
            public long AnchorAt;
            // A cut (slash joint): the edge's direction into the part and the
            // deepest the blade line may sit past the entry along it.
            public Vec3 CutN; public float CutMax;
            public float AngleLimit, AlongMin;
            // Offset still shown after the blade came out, glided away.
            public Vec3 Residual; public long ResidualAt; public float Glide = GlideSeconds;
            public Vec3 Held; public bool Holding; // a shield's or a blade's hold on the hand
            // An axe bitten into a part: the shown hand, fixed on it.
            public MatrixFrame Bite; public long BiteAt;
            // A grabbing hand: offset from the controller hand to the
            // grabbed point, set by the physics each tick.
            public Vec3 Grip; public long GripAt;
            // The held blade in the hand frame, from the last physics tick.
            public Vec3 BladeGrip, BladeTip; public long BladeAt;
        }
        private static readonly State[] States = { new State(), new State() };
        private const float MaxSeparation = .9f, GlideSeconds = .12f, AnchorSmoothing = .025f;
        private static readonly long Fresh = Stopwatch.Frequency / 10;

        // The physics, from the raw controller blade of this frame.
        public static void SetPierce(HandRole role, Vec3 anchor, Vec3 partAxis, float angleLimit, float alongMin, Vec3 rawGrip, Vec3 rawTip)
        {
            State s = States[(int)role];
            if (Stopwatch.GetTimestamp() - s.At > Fresh) { s.Pierced = false; return; }
            s.CutN = Vec3.Zero;
            s.GripLocal = s.Raw.TransformToLocalNonOrthogonal(rawGrip);
            s.TipLocal = s.Raw.TransformToLocalNonOrthogonal(rawTip);
            // The shown weapon follows the part through a short low-pass: the
            // B&S weapon is a rigidbody with inertia, not glued to the part's
            // every solver jitter.
            long stamp = Stopwatch.GetTimestamp();
            float since = s.AnchorAt == 0 ? 1 : (stamp - s.AnchorAt) / (float)Stopwatch.Frequency;
            s.AnchorAt = stamp;
            if (s.Pierced && InteractionMath.IsFinite(s.Anchor) && InteractionMath.IsFinite(anchor) && since < .1f && (anchor - s.Anchor).Length < .3f)
                anchor = s.Anchor + (anchor - s.Anchor) * (1 - (float)Math.Exp(-since / AnchorSmoothing));
            s.Anchor = anchor; s.PartAxis = partAxis; s.AngleLimit = angleLimit; s.AlongMin = alongMin;
            s.Pierced = InteractionMath.IsFinite(anchor) && (s.TipLocal - s.GripLocal).Length > .05f;
        }
        // Damager slash joint: x (the flat) locked, y (along the blade) free to
        // slide, z (the edge) limited to the material's velocity depth.
        public static void SetCut(HandRole role, Vec3 anchor, Vec3 edge, float maxDepth, Vec3 rawGrip, Vec3 rawTip)
        {
            SetPierce(role, anchor, Vec3.Zero, 0, float.NegativeInfinity, rawGrip, rawTip);
            State s = States[(int)role];
            if (!s.Pierced || !InteractionMath.IsFinite(edge) || edge.Length < .5f) { s.Pierced = false; return; }
            s.CutN = edge.NormalizedCopy(); s.CutMax = maxDepth;
        }
        public static void SetGrip(HandRole role, Vec3 offset)
        {
            State s = States[(int)role];
            if (!InteractionMath.IsFinite(offset)) return;
            s.Grip = InteractionMath.ClampMagnitude(offset, MaxSeparation); s.GripAt = Stopwatch.GetTimestamp();
        }
        public static void ClearGrip(HandRole role)
        {
            State s = States[(int)role];
            if (s.GripAt == 0) return;
            if (Stopwatch.GetTimestamp() - s.GripAt <= Fresh) { s.Residual = s.Grip; s.ResidualAt = Stopwatch.GetTimestamp(); s.Glide = GlideSeconds; }
            s.GripAt = 0; s.Grip = Vec3.Zero;
        }
        // Tales of Glory WepScript.OnCollisionEnter: the weapon is a
        // rigidbody that cannot pass a body; struck, it takes a recoil of
        // -0.4 x its velocity (a third on flesh and a body, half on armour),
        // clamped at 5 m/s and damped x0.9 a step, while the hand joint pulls
        // it back to the controller. A blade dealt through a fighting enemy
        // (no joint, the AI keeps fighting) crossed the body between two
        // frames, faster than VrCollide keeps it out: the shown blade is put
        // back at the contact, kicked back by the recoil, and glides to the
        // controller (B&S's hand joint catching up), longer off armour.
        public const float RecoilMult = .4f, RecoilClamp = 5, RecoilSeconds = .2f, MaxImpactOffset = .35f, ArmourGlide = .2f;
        public static void Impact(HandRole role, Vec3 stop, Vec3 velocity, bool armour)
        {
            State s = States[(int)role];
            if (s.Pierced || !InteractionMath.IsFinite(stop) || !InteractionMath.IsFinite(velocity)) return;
            Vec3 recoil = InteractionMath.ClampMagnitude(velocity * (-RecoilMult / (armour ? 2 : 3)), RecoilClamp);
            // A velocity change damped x0.9 every 20 ms travels 0.2 s of it.
            Vec3 offset = InteractionMath.ClampMagnitude(stop + recoil * (RecoilSeconds * .5f), MaxImpactOffset);
            long now = Stopwatch.GetTimestamp();
            // A shown offset still gliding back is not snapped away.
            if (s.ResidualAt != 0)
            {
                float t = (float)((now - s.ResidualAt) / (double)Stopwatch.Frequency) / s.Glide;
                if (t < 1 && (s.Residual * (1 - t)).Length > offset.Length) return;
            }
            s.Residual = offset; s.ResidualAt = now; s.Glide = armour ? ArmourGlide : GlideSeconds;
        }

        // The shown hand while an axe is bitten in (set every physics tick).
        public static void SetBite(HandRole role, MatrixFrame shown)
        {
            State s = States[(int)role];
            if (!InteractionMath.IsFinite(shown.origin)) return;
            s.Bite = shown; s.BiteAt = Stopwatch.GetTimestamp();
        }
        private static bool Biting(State s, long now) => s.BiteAt != 0 && now - s.BiteAt <= Fresh;
        // The controller's hand frame this frame (before any hold).
        public static bool RawHand(HandRole role, out MatrixFrame raw)
        {
            State s = States[(int)role];
            raw = s.Raw;
            return s.At != 0 && Stopwatch.GetTimestamp() - s.At <= Fresh;
        }

        public static void Clear(HandRole role)
        {
            State s = States[(int)role];
            if (Biting(s, Stopwatch.GetTimestamp())) { s.Residual = s.Bite.origin - s.Raw.origin; s.ResidualAt = Stopwatch.GetTimestamp(); s.Glide = GlideSeconds; }
            s.BiteAt = 0;
            if (s.Pierced) { s.Residual = s.Shown.origin - s.Raw.origin; s.ResidualAt = Stopwatch.GetTimestamp(); s.Glide = GlideSeconds; }
            s.Pierced = false;
        }

        // A blade measured on the shown weapon, mapped back to where the
        // controller holds it this frame.
        public static void ToRaw(HandRole role, ref Vec3 grip, ref Vec3 tip)
        {
            State s = States[(int)role];
            if (Stopwatch.GetTimestamp() - s.At > Fresh) return;
            // Held in a body, the blade comes from the controller alone, with
            // its place in the hand learnt before the entry. Mapped from the
            // shown weapon (drawn a frame earlier, following the falling part)
            // the part's own motion leaked into the blade the physics follows:
            // the body pulled itself along it, towards the player.
            if (s.Pierced && s.BladeAt != 0)
            {
                Vec3 cg = s.Raw.TransformToParent(s.BladeGrip), ct = s.Raw.TransformToParent(s.BladeTip);
                if (InteractionMath.IsFinite(cg) && InteractionMath.IsFinite(ct)) { grip = cg; tip = ct; }
                return;
            }
            Vec3 g = s.Shown.TransformToLocalNonOrthogonal(grip), t = s.Shown.TransformToLocalNonOrthogonal(tip);
            Vec3 rg = s.Raw.TransformToParent(g), rt = s.Raw.TransformToParent(t);
            if (InteractionMath.IsFinite(rg) && InteractionMath.IsFinite(rt)) { grip = rg; tip = rt; }
        }
        // The shown blade (world) mapped into the hand frame shown this frame.
        public static void LearnBlade(HandRole role, Vec3 grip, Vec3 tip)
        {
            State s = States[(int)role];
            if (Stopwatch.GetTimestamp() - s.At > Fresh) return;
            // The blade sits fixed in the hand: while it is held in a body the
            // shown weapon moves with the part and a frame-late measure of it
            // would drift; the one learnt before the entry is kept.
            if (s.Pierced && s.BladeAt != 0) { s.BladeAt = Stopwatch.GetTimestamp(); return; }
            Vec3 g = s.Shown.TransformToLocalNonOrthogonal(grip), t = s.Shown.TransformToLocalNonOrthogonal(tip);
            if (!InteractionMath.IsFinite(g) || !InteractionMath.IsFinite(t)) return;
            s.BladeGrip = g; s.BladeTip = t; s.BladeAt = Stopwatch.GetTimestamp();
        }
        public static float Separation(HandRole role)
        {
            State s = States[(int)role];
            return Stopwatch.GetTimestamp() - s.At > Fresh ? 0 : (s.Shown.origin - s.Raw.origin).Length;
        }

        private static MatrixFrame Constrain(State s, MatrixFrame raw)
        {
            MatrixFrame w = raw;
            if (s.CutN.LengthSquared > .5f)
            {
                Vec3 g = w.TransformToParent(s.GripLocal), t = w.TransformToParent(s.TipLocal);
                Vec3 a = t - g;
                if (a.Length < .05f) return raw;
                a.Normalize();
                Vec3 n = s.CutN - a * InteractionMath.Dot(s.CutN, a);
                if (n.Length < 1e-3f) return raw;
                n.Normalize();
                Vec3 f = Cross(a, n);
                Vec3 d = g + a * InteractionMath.Dot(s.Anchor - g, a) - s.Anchor;
                Vec3 move = -f * InteractionMath.Dot(d, f);
                float depth = InteractionMath.Dot(d, n);
                if (depth > s.CutMax) move -= n * (depth - s.CutMax);
                w.origin += move;
                Vec3 off = w.origin - raw.origin;
                if (off.Length > MaxSeparation) w.origin = raw.origin + off * (MaxSeparation / off.Length);
                return w;
            }
            // Angular limit about the entry point.
            Vec3 dir = w.rotation.TransformToParent(s.TipLocal - s.GripLocal);
            if (dir.Length > 1e-4f && s.PartAxis.LengthSquared > .5f)
            {
                dir.Normalize();
                float angle = (float)Math.Acos(Math.Max(-1, Math.Min(1, InteractionMath.Dot(dir, s.PartAxis)))) - s.AngleLimit;
                Vec3 k = Cross(dir, s.PartAxis);
                if (angle > 1e-3f && k.Length > 1e-5f)
                {
                    k.Normalize();
                    w.rotation.s = Rotate(w.rotation.s, k, angle);
                    w.rotation.f = Rotate(w.rotation.f, k, angle);
                    w.rotation.u = Rotate(w.rotation.u, k, angle);
                    w.origin = s.Anchor + Rotate(w.origin - s.Anchor, k, angle);
                }
            }
            // Lateral lock and depth limit.
            Vec3 grip = w.TransformToParent(s.GripLocal), tip = w.TransformToParent(s.TipLocal);
            Vec3 axis = tip - grip;
            if (axis.Length < .05f) return raw;
            axis.Normalize();
            float along = InteractionMath.Dot(s.Anchor - grip, axis);
            Vec3 shift = s.Anchor - (grip + axis * along);
            if (along < s.AlongMin) shift -= axis * (s.AlongMin - along);
            w.origin += shift;
            Vec3 total = w.origin - raw.origin;
            if (total.Length > MaxSeparation) w.origin = raw.origin + total * (MaxSeparation / total.Length);
            return w;
        }

        private static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        private static Vec3 Rotate(Vec3 v, Vec3 k, float angle)
        {
            float c = (float)Math.Cos(angle), sn = (float)Math.Sin(angle);
            return v * c + Cross(k, v) * sn + k * (InteractionMath.Dot(k, v) * (1 - c));
        }

        // VrCombatSound.Swing: BannerlordVR's swing whoosh plays on every hand
        // motion over 3 m/s; a blade pushed or drawn in a body played it again
        // and again, a sound of blows all through the stab.
        [HarmonyPatch]
        internal static class SwingSoundPatch
        {
            public static bool Prepare() => EnemyPhysicsSettings.Configured && (AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrCombatSound"), "Swing") != null);
            public static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrCombatSound"), "Swing");

            [HarmonyPrefix]
            public static bool Prefix() =>
                !EnemyPhysicsSettings.Enabled || (!EnemyPhysicsRuntime.HandHolds(0) && !EnemyPhysicsRuntime.HandHolds(1));
        }

        // VrMelee.Fire(agent, controller, ...): no engine attack from a hand
        // whose blade is held in a body (see EnemyPhysicsRuntime.HandHolds).
        [HarmonyPatch]
        internal static class EngineSwingPatch
        {
            private static int _skipped;
            public static bool Prepare() => EnemyPhysicsSettings.Configured && (AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrMelee"), "Fire") != null);
            public static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrMelee"), "Fire");

            [HarmonyPrefix]
            public static bool Prefix(VrHand controller)
            {
                if (!EnemyPhysicsSettings.Enabled || !EnemyPhysicsRuntime.HandHolds((int)controller)) return true;
                if (EnemyPhysicsSettings.Trace && _skipped++ % 20 == 0)
                    PhysicsLog.Info("ENGINE_ATTACK skipped " + _skipped + " time(s): the " + controller + " hand holds a body");
                return false;
            }
        }

        // VrCollide.Resolve(HandRole role, ref MatrixFrame world): the last
        // step before the arm IK places the hand and weapon.
        [HarmonyPatch]
        internal static class ResolvePatch
        {
            // Off unless enemy_physics: without it the game plays as without this patch.
            public static bool Prepare() => EnemyPhysicsSettings.Configured;
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrCollide"), "Resolve");

            // A blade held in a body skips VrCollide: it found the blade inside
            // the body's capsules every frame, a new contact bone after bone,
            // each with its contact haptic pulse (0.25-1): a held stab buzzed
            // like a string of blows. The joint places the blade instead.
            [HarmonyPrefix]
            public static bool Prefix(HandRole role, ref MatrixFrame world, out MatrixFrame __state, ref bool __result)
            {
                __state = world;
                State s = States[(int)role];
                if (!EnemyPhysicsSettings.Enabled || (!s.Pierced && !Biting(s, Stopwatch.GetTimestamp())) || Stopwatch.GetTimestamp() - s.At > Fresh) return true;
                __result = true;
                return false;
            }

            [HarmonyPostfix]
            public static void Postfix(HandRole role, ref MatrixFrame world, MatrixFrame __state, ref bool __result)
            {
                long perf = Perf.Start();
                try { Pose(role, ref world, __state, ref __result); }
                finally { Perf.Stop(Perf.Section.BladePose, perf); }
            }
            private static void Pose(HandRole role, ref MatrixFrame world, MatrixFrame __state, ref bool __result)
            {
                try
                {
                    State s = States[(int)role];
                    long now = Stopwatch.GetTimestamp();
                    s.Raw = __state; s.At = now;
                    bool biting = EnemyPhysicsSettings.Enabled && Biting(s, now);
                    if (biting)
                    {
                        // The axe stays where it bit; the hand holds it there.
                        world = s.Bite;
                        __result = true;
                    }
                    else if (EnemyPhysicsSettings.Enabled && s.Pierced)
                    {
                        // VrCollide would push the blade out of the body it is
                        // jointed into; the joint decides instead.
                        world = Constrain(s, __state);
                        __result = true;
                    }
                    else if (EnemyPhysicsSettings.Enabled && s.GripAt != 0 && now - s.GripAt <= Fresh)
                    {
                        // The hand holds the grabbed part; VrCollide would
                        // keep it out of the body it is holding.
                        world = __state;
                        world.origin += s.Grip;
                        __result = true;
                    }
                    else if (s.ResidualAt != 0)
                    {
                        float t = (float)((now - s.ResidualAt) / (double)Stopwatch.Frequency) / s.Glide;
                        if (t >= 1) s.ResidualAt = 0;
                        else world.origin += s.Residual * (1 - t);
                    }
                    // A shield stops the weapon and the hand (B&S), where
                    // VrCollide only turned the weapon about the grip.
                    Vec3 free = world.origin;
                    bool held = false;
                    if (EnemyPhysicsSettings.Enabled && !s.Pierced && !biting && s.BladeAt != 0 && now - s.BladeAt <= Fresh &&
                        ShieldBlock.Apply(role, ref world, s.Shown, s.BladeGrip, s.BladeTip))
                        __result = held = true;
                    // So does an enemy's blade (B&S weapons are rigidbodies).
                    if (EnemyPhysicsSettings.Enabled && !s.Pierced && !biting && s.BladeAt != 0 && now - s.BladeAt <= Fresh &&
                        BladeClash.Apply(role, ref world, s.Shown, s.BladeGrip, s.BladeTip))
                        __result = held = true;
                    // Let go by the shield or the blade, the hand glides back to
                    // the controller (the B&S hand joint catches up) instead of
                    // snapping there in one frame.
                    if (held) { s.Held = world.origin - free; s.Holding = true; }
                    else if (s.Holding)
                    {
                        s.Holding = false;
                        if (s.Held.Length > .01f && s.ResidualAt == 0) { s.Residual = s.Held; s.ResidualAt = now; s.Glide = GlideSeconds; world.origin += s.Residual; __result = true; }
                    }
                    s.Shown = world;
                }
                catch (Exception ex)
                {
                    PhysicsLog.Error("Physical blade pose failed; the shown weapon follows the controller.", ex);
                }
            }
        }

        // VrCollide treats only natively hostile agents as solid; the sandbox
        // dummies (no controller) are hostile by team.
        [HarmonyPatch]
        internal static class HostilePatch
        {
            // Off unless enemy_physics: without it the game plays as without this patch.
            public static bool Prepare() => EnemyPhysicsSettings.Configured;
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrCollide"), "Hostile");

            [HarmonyPostfix]
            public static void Postfix(Agent player, Agent agent, ref bool __result)
            {
                if (!__result && player != null && agent != null && EnemyBody.Hostile(agent, player)) __result = true;
            }
        }
    }
}
