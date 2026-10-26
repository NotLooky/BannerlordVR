using System;
using BannerlordVR.VR.Physics;

internal static class LivingLeaseTests
{
    private sealed class Host : ILivingRagdollHost
    {
        public bool Exists { get; set; } = true;
        public bool Alive { get; set; } = true;
        public bool Safe { get; set; } = true;
        public bool Paused { get; set; }
        public bool NativeActive { get; set; }
        public bool NativeDisabled { get; set; } = true;
        public bool FailStart;
        public bool PausedAtStart, SuppressedAtStart, DisabledAtResume;
        public int Starts, Ends, Suppressions, Resumes;
        public void BeginNative() { Starts++; PausedAtStart = Paused; SuppressedAtStart = Suppressions > 0; NativeDisabled = false; if (FailStart) throw new Exception("injected partial native activation failure"); }
        public void EndNative() { if (!Exists) throw new Exception("invalid native agent access"); Ends++; }
        public void SuppressMovement() { if (!Exists) throw new Exception("invalid native agent access"); Suppressions++; }
        public void ResumeDecisions() { if (!Exists) throw new Exception("invalid native agent access"); DisabledAtResume = NativeDisabled; Resumes++; }
    }
    public static void Run(Action<bool, string> check)
    {
        var host = new Host(); var lease = new LivingRagdollLease();
        check(lease.Begin(host, 1) && host.PausedAtStart && host.SuppressedAtStart && host.Starts == 1, "Native living activation acquires AI pause and suppresses inputs before activation");
        host.NativeActive = true; lease.Tick(1.01f);
        check(lease.OwnsPhysics && lease.Phase == LivingRagdollPhase.Simulating, "Forces allowed only after observed native activation");
        lease.RequestRelease(1.02f); lease.Tick(1.03f);
        check(host.Ends == 1 && host.Paused && lease.Occupied && !lease.OwnsPhysics, "End is asynchronous; do not resume AI while physics still owns the body");
        lease.RequestRelease(1.04f);
        check(host.Ends == 1, "Two releases cannot end native physics twice");
        host.NativeDisabled = true; host.NativeActive = false; lease.Tick(1.05f);
        check(!lease.Occupied && !host.Paused && host.Resumes == 1 && host.DisabledAtResume, "Resume AI only after observed native recovery");

        host = new Host(); lease = new LivingRagdollLease(); lease.Begin(host, 0);
        host.NativeActive = true; lease.Tick(.01f); lease.KeepHeld(.1f); lease.Tick(.2f);
        check(host.Ends == 0, "A second hand maintaining the same lease prevents premature release");
        host.Safe = false; lease.Tick(.21f);
        check(host.Ends == 1 && !lease.OwnsPhysics, "Mounting or a scripted action immediately requests native release");
        lease.EmergencyStop();
        check(!host.Paused && !lease.Occupied && host.Ends == 1, "Mission shutdown restores only owned pause and does not duplicate end");

        host = new Host(); lease = new LivingRagdollLease(); lease.Begin(host, 0);
        host.Alive = false; lease.Tick(.01f);
        check(!lease.Occupied && host.Ends == 0 && !host.Paused && host.Resumes == 0, "Real death transfers ragdoll to the engine without deactivating the corpse");

        host = new Host(); lease = new LivingRagdollLease(); lease.Begin(host, 0);
        host.Exists = false; lease.Tick(.01f);
        check(!lease.Occupied && host.Ends == 0 && host.Resumes == 0, "Deletion never calls the invalid native agent");

        host = new Host(); lease = new LivingRagdollLease(); lease.Begin(host, 0);
        lease.KeepHeld(.4f); lease.Tick(.4f);
        check(host.Ends == 1 && lease.Phase == LivingRagdollPhase.Recovering, "Activation deadline prevents indefinitely paused invisible physics");
        lease.Tick(2);
        check(lease.Faulted && !lease.Occupied && !host.Paused && !lease.Begin(host, 3), "Recovery timeout restores AI and locks out further activation");

        host = new Host(); lease = new LivingRagdollLease(); lease.Begin(host, 0);
        host.NativeActive = true; lease.Tick(.01f); lease.KeepHeld(6.1f); lease.Tick(6.1f);
        check(host.Ends == 1, "Native lifetime cap holds even if the user keeps squeezing");
        lease.EmergencyStop();

        host = new Host { Paused = true }; lease = new LivingRagdollLease(); lease.Begin(host, 0); lease.EmergencyStop();
        check(host.Paused && host.Suppressions == 0 && host.Resumes == 0, "External AI pause and its input ownership are preserved");

        host = new Host { FailStart = true }; lease = new LivingRagdollLease();
        try { lease.Begin(host, 0); check(false, "Expected injected activation failure"); }
        catch (Exception) { check(host.Starts == 1 && host.Ends == 1 && !host.Paused && !lease.Occupied, "Partial native activation failure still ends and restores ownership"); }

        host = new Host { Safe = false }; lease = new LivingRagdollLease();
        check(!lease.Begin(host, 0) && host.Starts == 0 && !host.Paused, "Unsafe agent rejected before any mutation");
        host.Safe = true; host.NativeDisabled = false;
        check(!lease.Begin(host, 0) && host.Starts == 0, "Do not take ownership of another system's ragdoll");

        host = new Host(); lease = new LivingRagdollLease(); lease.Begin(host, 0); host.Paused = false; lease.Tick(.01f);
        check(host.Ends == 1 && lease.Phase == LivingRagdollPhase.Recovering, "An external controller unpausing the agent initiates handback");
        lease.EmergencyStop();

        host = new Host(); lease = new LivingRagdollLease(); lease.Begin(host, 0); host.NativeActive = true;
        lease.KeepHeld(.01f, .6f); lease.KeepHeld(.1f); lease.Tick(.5f);
        check(host.Ends == 0 && lease.OwnsPhysics, "A held hand cannot shorten the pending impact-reaction lifetime");
        lease.Tick(.7f);
        check(host.Ends == 1, "Unmaintained impact reaction is bounded and hands back automatically");
        lease.EmergencyStop();
    }
}
