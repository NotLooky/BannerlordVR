namespace BannerlordVR.VR.Physics
{
    public enum GrabHandle { Hand, Wrist, Shoulder, Torso, Head, Neck, Legs }
    public enum GrabOutcome { Hold, Destabilize, Ungrab }
    [System.Flags]
    public enum ForceLiftCondition { None = 0, IdleOrPatrol = 1, Attack = 2, Combat = 4, Stagger = 8, StaggerFull = 16 }

    // Blade & Sorcery HandleRagdoll.ManagedUpdate with the human handle data
    // (Interactable_Human*.json): lifting the reference part above its
    // original creature-local height plus liftOffset triggers the handle's
    // liftBehaviour, and a head pulled below the original hip height always
    // destabilizes.
    public static class RagdollGrabRules
    {
        // HandleRagdollData.forceLiftGrabCondition: while the grabbed
        // creature is in one of these states, HandleRagdoll.ManagedUpdate
        // destabilizes it at once, so an unaware or reeling enemy grabbed by
        // the neck, the torso or the legs goes limp in the hand and can be
        // lifted (Interactable_HumanHeadNeck / Torso: IdleOrPatrol,
        // StaggerFull; Legs: also Attack and Stagger; Wrist only when
        // electrocuted; head, shoulder and hand never).
        public static ForceLiftCondition ForceLift(GrabHandle handle)
        {
            switch (handle)
            {
                case GrabHandle.Neck:
                case GrabHandle.Torso: return ForceLiftCondition.IdleOrPatrol | ForceLiftCondition.StaggerFull;
                case GrabHandle.Legs:
                    return ForceLiftCondition.IdleOrPatrol | ForceLiftCondition.Attack | ForceLiftCondition.Stagger | ForceLiftCondition.StaggerFull;
                default: return ForceLiftCondition.None;
            }
        }
        // CheckForceLiftCondition, from the B&S push context: an attacking
        // creature is also in combat.
        public static bool ForcesLift(GrabHandle handle, PushContext context)
        {
            ForceLiftCondition flags = ForceLift(handle);
            switch (context)
            {
                case PushContext.Idle: return (flags & ForceLiftCondition.IdleOrPatrol) != 0;
                case PushContext.Combat: return (flags & ForceLiftCondition.Combat) != 0;
                case PushContext.Attack:
                case PushContext.AttackJump: return (flags & (ForceLiftCondition.Attack | ForceLiftCondition.Combat)) != 0;
                case PushContext.Stagger: return (flags & ForceLiftCondition.Stagger) != 0;
                case PushContext.StaggerFull: return (flags & ForceLiftCondition.StaggerFull) != 0;
                default: return false;
            }
        }

        // Self: the grabbed part itself; otherwise the root (hips).
        public static bool LiftsSelf(GrabHandle handle) => handle == GrabHandle.Legs;
        public static float LiftOffset(GrabHandle handle) => handle == GrabHandle.Legs ? .1f : .05f;
        public const float UngrabLiftOffset = .15f;

        // HandleRagdollData.IkPositionWeight: how far the creature's animated
        // limb reaches for the grip. Torso, head, neck and shoulder handles
        // 0.25 (the body mostly keeps its own pose and resists), hand, wrist
        // and legs 1 (Interactable_Human*.json).
        public static float IkWeight(GrabHandle handle) =>
            handle == GrabHandle.Hand || handle == GrabHandle.Wrist || handle == GrabHandle.Legs ? 1 : .25f;
        public static GrabOutcome LiftBehaviour(GrabHandle handle)
        {
            switch (handle)
            {
                case GrabHandle.Neck:
                case GrabHandle.Legs: return GrabOutcome.Destabilize;
                case GrabHandle.Hand: return GrabOutcome.Hold;
                default: return GrabOutcome.Ungrab;
            }
        }

        // Creature.UpdateStep with HandleRagdollData.moveStep: head, neck,
        // shoulder and torso grabs walk the creature under its head with no
        // dead zone at 3x speed; a wrist grab follows the torso beyond 4 cm
        // (stepThreshold 0.2 x 0.2) at 1x; hands and legs do not step.
        public static bool Steps(GrabHandle handle) =>
            handle == GrabHandle.Head || handle == GrabHandle.Neck || handle == GrabHandle.Shoulder ||
            handle == GrabHandle.Torso || handle == GrabHandle.Wrist;
        public static bool StepsUnderHead(GrabHandle handle) => handle != GrabHandle.Wrist;
        public static float StepThreshold(GrabHandle handle) => handle == GrabHandle.Wrist ? .04f : 0;
        public static float StepSpeedMultiplier(GrabHandle handle) => handle == GrabHandle.Wrist ? 1 : 3;
        // Locomotion.forwardSpeed 0.2 scales the offset into a move input;
        // Bannerlord's walk tops out near 1.5 m/s for a dragged soldier.
        // Tuned in VR: at the B&S rate the root lagged and the standing pins
        // pulled a dragged enemy back to its spot ("the game refuses to move
        // it"); the root now closes most of the gap within a few tenths of a second.
        // Without a walking animation under it the stepped pose slides on its
        // feet: a 12 cm dead zone keeps the feet planted for sways and the
        // slide is held to a walk.
        public const float ForwardSpeed = .2f, StepRateScale = 13, MaxStepSpeed = 1.2f, SlideDeadZone = .12f;

        // Horizontal root displacement for one tick towards the reference.
        public static TaleWorlds.Library.Vec2 Step(GrabHandle handle, TaleWorlds.Library.Vec2 offset, float dt)
        {
            if (!Steps(handle) || !InteractionMath.IsFinite(dt) || dt <= 0 || dt > .1f ||
                !InteractionMath.IsFinite(offset.x) || !InteractionMath.IsFinite(offset.y)) return TaleWorlds.Library.Vec2.Zero;
            float distance = offset.Length;
            if (distance <= System.Math.Max(StepThreshold(handle), SlideDeadZone)) return TaleWorlds.Library.Vec2.Zero;
            float beyond = distance - SlideDeadZone;
            float speed = System.Math.Min(MaxStepSpeed, beyond * ForwardSpeed * StepSpeedMultiplier(handle) * StepRateScale);
            float travel = System.Math.Min(beyond, speed * dt);
            return offset * (travel / distance);
        }

        // Heights are creature-local, above the creature's ground origin.
        public static GrabOutcome Evaluate(GrabHandle handle, float referenceHeight, float referenceOriginalHeight,
            float headHeight, float rootOriginalHeight)
        {
            if (!InteractionMath.IsFinite(referenceHeight) || !InteractionMath.IsFinite(referenceOriginalHeight) ||
                !InteractionMath.IsFinite(headHeight) || !InteractionMath.IsFinite(rootOriginalHeight)) return GrabOutcome.Ungrab;
            GrabOutcome lift = LiftBehaviour(handle);
            // Without IK a pulled arm raises the hips by itself, so the grip
            // only slips past UngrabLiftOffset; falls keep the B&S offsets.
            float offset = lift == GrabOutcome.Ungrab ? UngrabLiftOffset : LiftOffset(handle);
            if (lift != GrabOutcome.Hold && referenceHeight > referenceOriginalHeight + offset) return lift;
            if (headHeight < rootOriginalHeight) return GrabOutcome.Destabilize;
            return GrabOutcome.Hold;
        }
    }
}
