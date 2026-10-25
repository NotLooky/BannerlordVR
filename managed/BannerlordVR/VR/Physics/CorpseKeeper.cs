using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // A dead agent's body stays physical until the engine files it as a
    // static corpse (TaleWorlds.Native, REA/Ghidra 0x18060dee0): that happens
    // once agent+0xc50 is clear and the death timer (start ticks +0xbd8, on
    // clock +0xbe0 of the clock table at module+0xda29b0, 10 us units) has run
    // past its duration (+0xbe8) by 3 s. A filed corpse's ragdoll never wakes
    // again (NeedsActivation forever). Near the player, as every B&S body
    // within reach is physical, the timer is restarted so the corpse can be
    // grabbed, dragged and lifted. Every read is checked before a write.
    internal static class CorpseKeeper
    {
        // A new mission: the last one's agents and entities are gone.
        internal static void ForgetMission() { Nudged.Clear(); Awake.Clear(); DiedAt.Clear(); }
        // When each corpse was first seen dead: it is kept physical
        // CorpseSeconds from then (or for good with KeepNearCorpses).
        private static readonly System.Collections.Generic.Dictionary<Agent, float> DiedAt = new System.Collections.Generic.Dictionary<Agent, float>();
        public static bool Fresh(Agent agent, float now)
        {
            if (EnemyPhysicsSettings.KeepNearCorpses) return true;
            if (!DiedAt.TryGetValue(agent, out float died) || now < died) { DiedAt[agent] = now; died = now; }
            return now - died < EnemyPhysicsSettings.CorpseSeconds;
        }
        private const int TimerStart = 0xbd8, TimerClock = 0xbe0, TimerDuration = 0xbe8, Filed = 0xc50;
        private const long ClockTable = 0xda29b0;
        private static readonly FieldInfo AgentPointer = typeof(Agent).GetField("_pointer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static long _module = -1;
        private static bool _off, _logged;

        private static long Module()
        {
            if (_module >= 0) return _module;
            _module = 0;
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (string.Equals(module.ModuleName, "TaleWorlds.Native.dll", StringComparison.OrdinalIgnoreCase))
                { _module = module.BaseAddress.ToInt64(); break; }
            return _module;
        }

        // Keep this corpse from being filed; false when it cannot be.
        public static bool Keep(Agent agent, float missionTime)
        {
            if (_off || agent == null || AgentPointer == null ||
                (agent.State != AgentState.Killed && agent.State != AgentState.Unconscious) || !Fresh(agent, missionTime)) return false;
            try
            {
                long module = Module();
                long ptr = (long)(ulong)(UIntPtr)AgentPointer.GetValue(agent);
                if (module == 0 || ptr == 0) return Stop("no native agent or module");
                if (Marshal.ReadByte(new IntPtr(ptr + Filed)) != 0) return false; // already filed
                int clock = Marshal.ReadInt32(new IntPtr(ptr + TimerClock));
                if (clock < 0 || clock > 15) return Stop("clock index " + clock);
                long now = Marshal.ReadInt64(new IntPtr(module + ClockTable + clock * 8L));
                long start = Marshal.ReadInt64(new IntPtr(ptr + TimerStart));
                float duration = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(new IntPtr(ptr + TimerDuration))), 0);
                double elapsed = (now - start) * 1e-5;
                if (elapsed < -1 || elapsed > 3600 || float.IsNaN(duration) || duration < 0 || duration > 3600)
                    return Stop(string.Format(CultureInfo.InvariantCulture, "timer elapsed={0:F1} duration={1:F1}", elapsed, duration));
                // The same timer first ends the death animation: past its
                // duration the engine starts the ragdoll and drops the weapons
                // (0x1805d34e0), and files the corpse 3 s later. Held from the
                // start, the weapons never dropped. It is only held back once
                // past that step, a second after it, short of the filing.
                if (elapsed < duration + 2) return true;
                Marshal.WriteInt64(new IntPtr(ptr + TimerStart), now - (long)((duration + 1) * 1e5));
                if (!_logged)
                {
                    _logged = true;
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "CORPSE kept physical agent={0} (death timer elapsed {1:F1} s of {2:F1} + 3 s)", agent.Index, elapsed, duration));
                }
                return true;
            }
            catch (Exception ex) { _off = true; PhysicsLog.Error("Keeping a corpse physical failed; corpses are filed as usual.", ex); return false; }
        }

        // A body the player's blow just killed goes ragdoll at once: the
        // death timer is set to its end, past which the engine starts the
        // ragdoll and drops the weapons (0x1805d34e0). It played its death
        // animation first, a second or more with the blade passing through.
        public static void RagdollNow(Agent agent)
        {
            if (_off || agent == null || AgentPointer == null || EnemyPhysicsSettings.CorpseSeconds <= 0 ||
                (agent.State != AgentState.Killed && agent.State != AgentState.Unconscious)) return;
            try
            {
                long module = Module();
                long ptr = (long)(ulong)(UIntPtr)AgentPointer.GetValue(agent);
                if (module == 0 || ptr == 0 || Marshal.ReadByte(new IntPtr(ptr + Filed)) != 0) return;
                int clock = Marshal.ReadInt32(new IntPtr(ptr + TimerClock));
                if (clock < 0 || clock > 15) { Stop("clock index " + clock); return; }
                long now = Marshal.ReadInt64(new IntPtr(module + ClockTable + clock * 8L));
                long start = Marshal.ReadInt64(new IntPtr(ptr + TimerStart));
                float duration = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(new IntPtr(ptr + TimerDuration))), 0);
                double elapsed = (now - start) * 1e-5;
                if (elapsed < -1 || elapsed > 3600 || float.IsNaN(duration) || duration < 0 || duration > 3600)
                { Stop(string.Format(CultureInfo.InvariantCulture, "timer elapsed={0:F1} duration={1:F1}", elapsed, duration)); return; }
                if (elapsed >= duration) return;
                Marshal.WriteInt64(new IntPtr(ptr + TimerStart), now - (long)(duration * 1e5));
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "CORPSE agent={0} ragdoll now, {1:F1} s of death animation skipped", agent.Index, duration - elapsed));
            }
            catch (Exception ex) { _off = true; PhysicsLog.Error("Starting a corpse's ragdoll failed; corpses are filed as usual.", ex); }
        }

        // The ragdoll of a body lying still goes to sleep and the engine
        // turns it off (Disabled; a wake from there stayed NeedsActivation on a
        // corpse). A force of half a newton every half second keeps PhysX from
        // putting it to sleep: addForce wakes its actor.
        private static readonly System.Collections.Generic.Dictionary<Agent, float> Nudged = new System.Collections.Generic.Dictionary<Agent, float>();
        private static bool _awakeLogged;
        private static readonly System.Collections.Generic.HashSet<Agent> Awake = new System.Collections.Generic.HashSet<Agent>();
        public static void KeepAwake(EnemyBody body, float now)
        {
            if (body?.Agent == null || body.Skeleton == null) return;
            if (Nudged.TryGetValue(body.Agent, out float last) && now - last < .5f && now >= last) return;
            Nudged[body.Agent] = now;
            TaleWorlds.Engine.RagdollState state = body.Skeleton.GetCurrentRagdollState();
            if (state != TaleWorlds.Engine.RagdollState.Active)
            {
                if (Awake.Remove(body.Agent))
                    PhysicsLog.Info("CORPSE agent=" + body.Agent.Index + " ragdoll went " + state + " although kept awake");
                return;
            }
            Awake.Add(body.Agent);
            TaleWorlds.Library.Vec3 nudge = new TaleWorlds.Library.Vec3(0, 0, .5f);
            body.Agent.ApplyForceOnRagdoll(0, in nudge);
            if (!_awakeLogged) { _awakeLogged = true; PhysicsLog.Info("CORPSE kept awake agent=" + body.Agent.Index); }
        }

        private static bool Stop(string why)
        {
            _off = true;
            PhysicsLog.Info("CORPSE keeping off: " + why + "; corpses are filed as usual.");
            return false;
        }
    }
}
