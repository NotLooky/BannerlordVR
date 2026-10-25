using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class ActiveRagdollTests
{
    public static void Run(Action<bool, string> check)
    {
        PinDrive(check);
        RotationPins(check);
        Push(check);
        Cycle(check);
        Grab(check);
    }

    private static void Grab(Action<bool, string> check)
    {
        check(RagdollGrabRules.Evaluate(GrabHandle.Neck, 1.06f, 1, 1.6f, 1) == GrabOutcome.Destabilize,
            "Lifting the hips 6 cm by the neck makes the creature fall (Interactable_HumanHeadNeck)");
        check(RagdollGrabRules.Evaluate(GrabHandle.Neck, 1.04f, 1, 1.6f, 1) == GrabOutcome.Hold, "Within liftOffset the grab holds");
        check(RagdollGrabRules.Evaluate(GrabHandle.Torso, 1.2f, 1, 1.6f, 1) == GrabOutcome.Ungrab, "Lifting by the torso slips out of the grip");
        check(RagdollGrabRules.Evaluate(GrabHandle.Torso, 1.1f, 1, 1.6f, 1) == GrabOutcome.Hold, "A 10 cm hip rise from a pulled arm keeps the grip");
        check(RagdollGrabRules.Evaluate(GrabHandle.Hand, 1.5f, 1, 1.6f, 1) == GrabOutcome.Hold, "A held hand has no lift behaviour");
        check(RagdollGrabRules.Evaluate(GrabHandle.Legs, .55f, .5f, 1.6f, 1) == GrabOutcome.Hold &&
              RagdollGrabRules.Evaluate(GrabHandle.Legs, .61f, .5f, 1.6f, 1) == GrabOutcome.Destabilize,
            "Legs use their own height and a 10 cm offset");
        check(RagdollGrabRules.Evaluate(GrabHandle.Hand, 1, 1, .9f, 1) == GrabOutcome.Destabilize,
            "Pulling the head below the original hip height destabilizes");
        check(RagdollGrabRules.Evaluate(GrabHandle.Hand, float.NaN, 1, 1, 1) == GrabOutcome.Ungrab, "Invalid heights release the grip");

        var drive = new RagdollPinDrive();
        drive.Configure(new[] { 14f, 5 }, new[] { -1, 0 });
        Vec3[] target = { new Vec3(0, 0, 1), new Vec3(0, 0, 1.5f) };
        var pos = (Vec3[])target.Clone(); var force = new Vec3[2];
        drive.State = PinState.Destabilized;
        check(!drive.SetHandle(2, 1, Vec3.Zero) && !drive.SetHandle(0, 5, Vec3.Zero), "Invalid handle refused");
        check(drive.SetHandle(0, 1, new Vec3(.2f, 0, 1.5f)) && drive.Handled(1) && drive.AnyHandle, "Handle attaches a body");
        drive.Tick(pos, target, 1f / 90, force);
        check(force[1].x > 0 && force[1].x <= RagdollPinDrive.HandMaxForce, "The grip pulls the part towards the hand even when destabilized");
        // A blade joint: locked across the blade (x here), free along it (z).
        drive.SetHandle(0, 1, new Vec3(.2f, 0, 1.8f), Vec3.Up);
        drive.Tick(pos, target, 1f / 90, force);
        check(force[1].x > 0 && Math.Abs(force[1].z) < 1e-3f, "A blade joint pulls across the blade only");
        drive.ClearHandle(0);
        drive.Tick(pos, target, 1f / 90, force);
        check(!drive.AnyHandle && Math.Abs(force[1].x) < 1e-3f, "Releasing the grip removes its force");
        // Hanging a 5 kg head from the hand at 90 Hz with one step of latency.
        drive.State = PinState.Inert; drive.ResetVelocity();
        var p = new Vec3[] { target[0], target[1] }; var v = new Vec3[2]; var pending = new Vec3[2];
        Vec3 hand = new Vec3(.3f, 0, 1.6f); bool finite = true;
        for (int step = 0; step < 270; step++)
        {
            var applied = (Vec3[])pending.Clone();
            drive.SetHandle(0, 1, hand);
            drive.Tick(p, target, 1f / 90, pending);
            v[1] += (applied[1] * (1f / 5) - Vec3.Up * 9.81f) * (1f / 90);
            p[1] += v[1] * (1f / 90);
            finite &= InteractionMath.IsFinite(p[1]);
        }
        check(finite && (p[1] - hand).Length < .25f && v[1].Length < .5f, "A held part settles under the grip without oscillating");
    }

    private static void RotationPins(Action<bool, string> check)
    {
        Vec3 x = new Vec3(1, 0, 0), y = new Vec3(0, 1, 0), z = new Vec3(0, 0, 1);
        check(RotationPin.Error(x, y, z, x, y, z).Length < 1e-6f, "No rotation error on the posed orientation");
        // Target turned 0.5 rad about z.
        float c = (float)Math.Cos(.5), s = (float)Math.Sin(.5);
        Vec3 e = RotationPin.Error(x, y, z, new Vec3(c, s, 0), new Vec3(-s, c, 0), z);
        check(Math.Abs(e.z - .5f) < 1e-4f && Math.Abs(e.x) < 1e-5f && Math.Abs(e.y) < 1e-5f, "Error is the axis-angle from current to target");
        Vec3 a = RotationPin.Acceleration(e, Vec3.Zero, 5, 1, 1, 1);
        check(a.z > 0 && a.z <= RagdollPinDrive.MaxNaturalFrequency * RagdollPinDrive.MaxNaturalFrequency * .5f + 1e-3f,
            "The drive turns the part towards its pose within the natural-frequency cap");
        check(RotationPin.Acceleration(Vec3.Zero, new Vec3(0, 0, 3), 5, 1, 1, 1).z < 0, "Spin is damped");
        // A 5 kg part, 90 Hz explicit drive with one step of latency, settles.
        float angle = 1, w = 0, pending = 0; bool finite = true;
        for (int i = 0; i < 270; i++)
        {
            float applied = pending;
            pending = RotationPin.Acceleration(new Vec3(0, 0, -angle), new Vec3(0, 0, w), 5, 1, 1, 1).z;
            w += applied / 90; angle += w / 90; finite &= !float.IsNaN(angle);
        }
        check(finite && Math.Abs(angle) < .05f && Math.Abs(w) < .2f, "A twisted part settles on its pose without oscillating");
    }

    private static void PinDrive(Action<bool, string> check)
    {
        var drive = new RagdollPinDrive();
        check(!drive.Configure(new[] { 10f, 3 }, new[] { 0, 0 }), "A body cannot be its own parent");
        check(!drive.Configure(new[] { 10f, 3 }, new[] { -1, -1 }), "Exactly one root body");
        check(!drive.Configure(new[] { 10f, float.NaN }, new[] { -1, 0 }), "Invalid body mass refused");
        // Pelvis, torso, head and a hand: masses like a light Bannerlord ragdoll.
        float[] mass = { 14, 18, 5, 1 };
        int[] parent = { -1, 0, 1, 1 };
        check(drive.Configure(mass, parent) && Math.Abs(drive.TotalMass - 38) < .001f, "Valid body tree configures");
        Vec3[] target = { new Vec3(0, 0, 1), new Vec3(0, 0, 1.35f), new Vec3(0, 0, 1.65f), new Vec3(.3f, 0, 1.3f) };
        var force = new Vec3[4];
        var pos = (Vec3[])target.Clone();
        drive.State = PinState.Standing;
        drive.Tick(pos, target, 1f / 90, force);
        drive.Tick(pos, target, 1f / 90, force);
        bool weight = true;
        for (int i = 0; i < 4; i++) weight &= Math.Abs(force[i].z - mass[i] * 9.81f) < .01f && Math.Abs(force[i].x) < 1e-4f;
        check(weight, "Standing on the animated pose cancels gravity on every part, as the B&S modifier does");
        drive.State = PinState.Inert;
        drive.Tick(pos, target, 1f / 90, force);
        check(force[0].LengthSquared == 0 && force[3].LengthSquared == 0, "Inert applies no pin and keeps gravity");
        drive.State = PinState.Destabilized;
        var bent = (Vec3[])target.Clone();
        bent[3] = new Vec3(.1f, 0, 1.1f);
        drive.ResetVelocity();
        drive.Tick(bent, target, 1f / 90, force);
        Vec3 sum = force[0] + force[1] + force[2] + force[3];
        check(sum.Length < 1e-3f && force[3].LengthSquared > 0, "Destabilized joint pins are internal: they restore shape without lifting the body");
        drive.State = PinState.Standing; drive.Multiplier = 0;
        drive.ResetVelocity();
        drive.Tick(bent, target, 1f / 90, force);
        check(Math.Abs(force[3].z - 9.81f) < .01f && Math.Abs(force[3].x) < 1e-4f, "Zero pin multiplier leaves only the gravity modifier");
        drive.Multiplier = 1;
        var far = (Vec3[])target.Clone(); far[1] = new Vec3(5, 0, 1.35f);
        drive.ResetVelocity();
        drive.Tick(far, target, 1f / 90, force);
        check(force[1].Length < RagdollPinDrive.MaxPosition + 18 * 9.81f + RagdollPinDrive.MaxRotation / .35f * 3 + 1,
            "Position drive respects the B&S maximum force");
        check(!drive.Tick(pos, target, float.NaN, force) && force[0].LengthSquared == 0, "Invalid step clears forces");

        // Independent explicit integration, one PhysX step of latency, at the
        // measured 90 Hz cadence: the light hand must not oscillate.
        foreach (PinState state in new[] { PinState.Standing, PinState.Destabilized })
        {
            drive.State = state; drive.Multiplier = 1; drive.ResetVelocity();
            var p = new Vec3[4]; var v = new Vec3[4]; var pending = new Vec3[4];
            for (int i = 0; i < 4; i++) p[i] = target[i] + new Vec3(.08f * (i % 2 == 0 ? 1 : -1), .05f, -.06f);
            bool finite = true; float shapeError = 0;
            float h = 1f / 90;
            for (int step = 0; step < 270; step++)
            {
                var applied = (Vec3[])pending.Clone();
                drive.Tick(p, target, h, pending);
                for (int i = 0; i < 4; i++)
                {
                    v[i] += (applied[i] * (1 / mass[i]) - Vec3.Up * 9.81f) * h;
                    p[i] += v[i] * h;
                    finite &= InteractionMath.IsFinite(p[i]);
                }
            }
            for (int i = 1; i < 4; i++)
                shapeError = Math.Max(shapeError, ((p[i] - p[parent[i]]) - (target[i] - target[parent[i]])).Length);
            if (state == PinState.Standing)
            {
                float err = 0; for (int i = 0; i < 4; i++) err = Math.Max(err, (p[i] - target[i]).Length);
                check(finite && err < .03f, "Standing pins settle every part on its animated target at 90 Hz with one step of latency");
            }
            else
                check(finite && p[0].z < target[0].z - 1 && shapeError < .25f,
                    "Destabilized body falls under gravity while weakened joint pins keep its limbs in shape");
        }
    }

    private static void Push(Action<bool, string> check)
    {
        check(PushReaction.Level(PushMaterial.Punch, 1.9f) == 0 && PushReaction.Level(PushMaterial.Punch, 5) == 1 &&
              PushReaction.Level(PushMaterial.Punch, 30) == 4, "Punch push levels follow the B&S DamageModifier_Punch thresholds");
        check(PushReaction.Level(PushMaterial.Punch, 30, true) == 0, "Thrown punches never push (infinite throw threshold)");
        check(PushReaction.Level(PushMaterial.Pierce, 7) == 0 && PushReaction.Level(PushMaterial.Pierce, 7, true) == 1,
            "Thrown piercing uses its own lower thresholds");
        check(PushReaction.Level(PushMaterial.Slash, float.NaN) == 0, "Invalid impact speed is not a push");
        check(PushReaction.HitEffect(2, PushBodyZone.Torso, PushContext.Idle) == PushEffect.Destabilize,
            "An idle human is knocked down by a level 2 hit");
        check(PushReaction.HitEffect(2, PushBodyZone.Torso, PushContext.Combat) == PushEffect.StaggerLight,
            "A guarding human only staggers lightly from the same hit");
        check(PushReaction.HitEffect(1, PushBodyZone.HeadOrLeg, PushContext.AttackJump) == PushEffect.Destabilize,
            "Leg or head hits during a jump attack destabilize");
        check(PushReaction.HitEffect(0, PushBodyZone.Hand, PushContext.Attack) == PushEffect.StaggerLight &&
              PushReaction.HitEffect(0, PushBodyZone.Torso, PushContext.Attack) == PushEffect.None,
            "Hand-specific level 0 row replaces the generic row");
        check(PushReaction.HitEffect(9, PushBodyZone.Torso, PushContext.StaggerFull) == PushEffect.Destabilize,
            "Levels above the human table use its highest row");
        check(PushReaction.GrabEffect(1, PushContext.Idle) == PushEffect.Destabilize &&
              PushReaction.GrabEffect(0, PushContext.Combat) == PushEffect.StaggerMedium, "Grab and throw push table");
        check(PushReaction.Recoil(PushEffect.Destabilize) == 2 && PushReaction.Recoil(PushEffect.StaggerMedium) == .6f,
            "Recoil amounts match BrainModuleHitReaction");
    }

    private static void Cycle(Action<bool, string> check)
    {
        var cycle = new ActiveRagdollCycle();
        cycle.Begin(false);
        check(cycle.DriveState == PinState.Standing && cycle.CancelGravity && cycle.PinMultiplier == 1, "Standing at full pins without gravity");
        cycle.Destabilize();
        check(cycle.DriveState == PinState.Destabilized && !cycle.CancelGravity, "Destabilize restores gravity");
        for (int i = 0; i < 90; i++) cycle.Tick(1f / 90, false, 4);
        check(cycle.Fall == FallState.Falling && cycle.Phase == ActiveRagdollPhase.Destabilized, "Airborne body keeps falling");
        for (int i = 0; i < 200; i++) cycle.Tick(1f / 90, true, .5f);
        check(cycle.Fall == FallState.Stabilizing, "Under 3 s on the ground it is only stabilizing");
        cycle.Tick(1f / 90, true, 1.5f);
        check(cycle.Fall == FallState.NearGround, "Rolling faster than 1 m/s resets the ground timer");
        for (int i = 0; i < 280; i++) cycle.Tick(1f / 90, true, .2f);
        check(cycle.Phase == ActiveRagdollPhase.StandingUp && !cycle.CancelGravity && cycle.PinMultiplier < .05f,
            "After 3 s stabilized on the ground the creature starts standing up from zero pins with gravity");
        float last = 0; bool monotonic = true; bool committed = false;
        for (int i = 0; i < 300 && cycle.Phase == ActiveRagdollPhase.StandingUp; i++)
        {
            cycle.Tick(1f / 90, true, .2f);
            monotonic &= cycle.PinMultiplier >= last - 1e-6f; last = cycle.PinMultiplier;
            if (cycle.GetUpCommitted && !committed) { committed = true; check(Math.Abs(cycle.PinMultiplier - .784f) < .02f, "Get-up commits at 70% of the rise"); }
        }
        check(monotonic && cycle.Phase == ActiveRagdollPhase.Recovered && cycle.PinMultiplier == 1 && cycle.CancelGravity,
            "The rise ramps pins up monotonically, then full standing pins");
        cycle.Begin(true);
        for (int i = 0; i < 300; i++) cycle.Tick(1f / 90, true, 0);
        cycle.Tick(1f / 90, true, 0);
        check(cycle.Phase == ActiveRagdollPhase.StandingUp, "Second fall reaches stand up");
        cycle.Destabilize();
        check(cycle.Phase == ActiveRagdollPhase.Destabilized && !cycle.GetUpCommitted, "A push during the rise cancels the get-up");
        cycle.Tick(float.NaN, true, 0);
        check(cycle.Phase == ActiveRagdollPhase.Destabilized, "Invalid time does not advance the cycle");
    }
}
