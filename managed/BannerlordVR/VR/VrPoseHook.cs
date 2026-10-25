using System;
using System.Threading;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR
{
    /// <summary>
    /// TEST 224. A script component on the player's agent entity, whose one job
    /// is to be called from INSIDE the engine's native animation pass.
    ///
    /// WHY THIS EXISTS
    ///
    /// Ten rounds of weapon work (TESTs 207-221) tried to win an argument with a
    /// native pass that re-derives every equipment frame from the animation, at
    /// render time, after every managed hook. TEST 216 read the call order out of
    /// the engine: nothing managed runs later, so writing later is finished as a
    /// strategy.
    ///
    /// Skeleton.EnableScriptDrivenPostIntegrateCallback is the hook that runs
    /// inside that pass, after the animation integrates and before the equipment
    /// is derived from it. A pose written there is not overwritten - it is what
    /// the derivation starts from, using the rig's own bindings.
    ///
    /// REGISTRATION IS AUTOMATIC. Module.AddSubModule hands every DotNetObject
    /// type in this assembly to Managed.AddTypes, keyed on Type.Name, and
    /// ScriptComponentBehavior is a DotNetObject. Do NOT call AddTypes here: it
    /// does Dictionary.Add, so a second registration of the same name throws.
    /// The class name is deliberately unlikely to collide with anything.
    ///
    /// WHAT IS NOT KNOWN, AND WHAT THIS BUILDS OUT
    ///
    /// Native picks the script to call - the managed glue receives a DotNetObject
    /// id, and Skeleton.EnableScriptDrivenPostIntegrateCallback takes no script.
    /// Whether that dispatcher walks an AGENT VISUAL's script list is not
    /// readable from the DLLs. pose_hook = probe answers it in one mission load:
    ///
    ///   1. "POSE HOOK FIRED" never appears  -> the hook is not routed; fall back
    ///      to owning the weapon visually (plan step 3).
    ///   2. It fires, the hand rises 0.20 m and the weapon rises with it
    ///      -> the whole approach is confirmed; port the IK sink (step 2).
    ///   3. It fires, the hand rises and the weapon does not
    ///      -> equipment is not derived downstream of this callback.
    ///
    /// The readings are logged the moment each one succeeds, because a native
    /// accessor that faults takes the process with it and the log is all there is.
    /// </summary>
    public class BvrPoseHookScript : ScriptComponentBehavior
    {
        // Parameterless, and public: the engine constructs the component by name.
        public BvrPoseHookScript()
        {
        }

        /// <summary>"off" (default) or "probe". "drive" is step 2 and does not
        /// exist until the probe has said which space the setters write in.</summary>
        internal static readonly string Mode =
            VrConfig.String("pose_hook", "off").Trim().ToLowerInvariant();

        /// <summary>What the callback returns. The base returns false and nothing
        /// shipped overrides it, so false is what the engine has always seen; it
        /// could equally mean "the pose changed, re-derive". A key rather than a
        /// rebuild so the two can be compared between launches.</summary>
        private static readonly bool ReturnValue = VrConfig.Bool("pose_hook_return", false);

        private static readonly float LiftMetres = VrConfig.Float("pose_hook_probe_lift", 0.20f);

        /// <summary>"entitial" sends the hand's entitial origin plus the lift;
        /// "delta" sends the lift alone, in case the setter takes a displacement.</summary>
        private static readonly string ProbeWrite =
            VrConfig.String("pose_hook_probe_write", "entitial").Trim().ToLowerInvariant();

        private static readonly bool ProbeQuat = VrConfig.Bool("pose_hook_probe_quat", false);

        private static int _mainThread = -1;

        private const int MaxReports = 8;
        private const int ReportEvery = 45;
        private const int MaxArmAttempts = 3;

        // Written on the main thread, read from wherever the engine calls us.
        private static volatile Skeleton _skeleton;
        private static volatile int _handBone = -1;

        private static UIntPtr _armedEntity;
        private static UIntPtr _armedSkeleton;
        private static int _armAttempts;
        private static bool _loggedMode;

        private static int _fires;
        private static int _reports;

        public static bool Wanted => Mode == "probe";

        /// <summary>
        /// Called every tick from VrWeaponHands with the resolved entity and
        /// skeleton. Cheap once armed: two pointer compares.
        /// </summary>
        public static void Arm(GameEntity entity, Skeleton skeleton, int handBone)
        {
            if (Mode == "off")
                return;

            if (!_loggedMode)
            {
                _loggedMode = true;
                if (Mode == "probe")
                    VrLog.Info("POSE HOOK: pose_hook = probe, return = " + ReturnValue
                               + ", lift = " + LiftMetres.ToString("F2") + " m");
                else
                    VrLog.Warn("POSE HOOK: pose_hook = '" + Mode + "' is not a mode; "
                               + "only off and probe exist. Nothing armed.");
            }

            if (!Wanted)
                return;

            try
            {
                // Refreshed every tick: the hand can change (bow swap) and the
                // callback has no other way to learn it.
                _skeleton = skeleton;
                _handBone = handBone;
                _mainThread = Thread.CurrentThread.ManagedThreadId;

                if (_armedEntity == entity.Pointer && _armedSkeleton == skeleton.Pointer)
                    return;

                if (_armAttempts >= MaxArmAttempts)
                    return;
                _armAttempts++;

                string name = typeof(BvrPoseHookScript).Name;

                if (!entity.HasScriptComponent(name))
                    entity.CreateAndAddScriptComponent(name, true);

                // The engine reports an unresolvable name by printing a warning
                // and handing back nothing, so the return value is no evidence.
                bool has = entity.HasScriptComponent(name);
                VrLog.Info("POSE HOOK: script component '" + name + "' on the agent entity: "
                           + (has ? "PRESENT" : "MISSING (the engine could not resolve the name)"));
                if (!has)
                    return;

                skeleton.EnableScriptDrivenPostIntegrateCallback();
                VrLog.Info("POSE HOOK: callback enabled on the agent skeleton ("
                           + skeleton.GetBoneCount() + " bones). Waiting for it to fire.");

                _armedEntity = entity.Pointer;
                _armedSkeleton = skeleton.Pointer;
            }
            catch (Exception ex)
            {
                _armAttempts = MaxArmAttempts;
                VrLog.Error("POSE HOOK: arming failed; giving up for this mission.", ex);
            }
        }

        /// <summary>Agent visuals are pooled and recycled natively, so a component
        /// added at runtime is not assumed to outlive its agent.</summary>
        public static void Reset()
        {
            _armedEntity = UIntPtr.Zero;
            _armedSkeleton = UIntPtr.Zero;
            _armAttempts = 0;
            _skeleton = null;
            _handBone = -1;
            _fires = 0;
            _reports = 0;
        }

        protected override bool SkeletonPostIntegrateCallback(AnimResult animResult)
        {
            try
            {
                Skeleton skeleton = _skeleton;
                sbyte bone = (sbyte)_handBone;

                int fire = Interlocked.Increment(ref _fires);
                if (fire == 1)
                    VrLog.Info("POSE HOOK FIRED, skeleton has "
                               + (skeleton != null ? skeleton.GetBoneCount().ToString() : "? (not yet known)")
                               + " bones");

                if (fire == 1)
                {
                    // Where the callback is called FROM. If our own
                    // UpdateEntitialFramesFromLocalFrames or the weapon refresh is
                    // in the trace, the callback is re-entrant on the main thread
                    // and animResult is just the live skeleton, not a scratch pose.
                    VrLog.Info("POSE HOOK thread " + Thread.CurrentThread.ManagedThreadId
                               + " (main thread " + _mainThread + ") stack:" + Environment.NewLine
                               + Environment.StackTrace);
                }

                if (skeleton == null || bone < 0 || Mode != "probe")
                    return ReturnValue;

                Transformation before = animResult.GetEntitialOutTransform(bone, skeleton);

                bool report = _reports < MaxReports && (fire == 1 || fire % ReportEvery == 0);
                if (report)
                {
                    // Reading 2: is the callback's getter the same space the IK
                    // already solves in, and is the skeleton's own entitial frame
                    // stale or current at this point in the pass.
                    Vec3 skel = skeleton.GetBoneEntitialFrameWithIndex(bone).origin;
                    VrLog.Info("POSE HOOK read: bone " + bone + " animResult.entitial = "
                               + Fmt(before.Origin) + "  skeleton.entitial = " + Fmt(skel)
                               + "  apart " + (before.Origin - skel).Length.ToString("F4") + " m");
                }

                // Reading 3: a deliberate lift on the main-hand bone. Applied on
                // every call, not just the reported ones, so it can be judged by
                // eye - the decisive question is whether the WEAPON moves too.
                //
                // WHAT IS SENT depends on pose_hook_probe_write. The setter is
                // named "displacement" while the getter is "entitial", and run 1
                // (entitial) read back a change of exactly zero, so the second
                // reading is that the setter wants a DELTA or a parent-local value.
                bool delta = ProbeWrite == "delta";
                Vec3 lift = new Vec3(0f, 0f, LiftMetres);
                animResult.SetOutBoneDisplacement(bone, delta ? lift : before.Origin + lift, skeleton);

                // Some engines only mark the bone dirty on the rotation setter.
                if (ProbeQuat)
                    animResult.SetOutQuat(bone, before.Rotation, skeleton);

                if (report)
                {
                    // Read straight back: if the setter takes entitial values the
                    // difference is the lift; if it takes parent-local values it is
                    // something else, and that is the answer about the space.
                    Transformation after = animResult.GetEntitialOutTransform(bone, skeleton);
                    Vec3 d = after.Origin - before.Origin;
                    _reports++;
                    Vec3 skelAfter = skeleton.GetBoneEntitialFrameWithIndex(bone).origin;
                    VrLog.Info("POSE HOOK write (" + ProbeWrite + (ProbeQuat ? "+quat" : "") + "): asked +"
                               + LiftMetres.ToString("F2") + " m up; animResult read-back moved " + Fmt(d)
                               + ", skeleton.entitial now " + Fmt(skelAfter) + " (report "
                               + _reports + "/" + MaxReports + ", fire " + fire + ")");
                    if (_reports == MaxReports)
                        VrLog.Info("POSE HOOK: reports done; the lift keeps applying, judge it by eye.");
                }
            }
            catch (Exception ex)
            {
                // A throw into the native pass is worse than a silent miss.
                if (Interlocked.Exchange(ref _reports, MaxReports) != MaxReports)
                    VrLog.Error("POSE HOOK: callback threw; going quiet.", ex);
            }

            return ReturnValue;
        }

        private static string Fmt(Vec3 v)
        {
            return "(" + v.x.ToString("F3") + ", " + v.y.ToString("F3") + ", " + v.z.ToString("F3") + ")";
        }
    }
}
