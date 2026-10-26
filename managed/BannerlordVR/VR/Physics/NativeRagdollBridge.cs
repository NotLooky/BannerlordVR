using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    internal static class NativeRagdollBridge
    {
        private const string EngineHash = "2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a";
        private static readonly FieldInfo AgentPointer = typeof(Agent).GetField("_pointer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo MissionPointer = typeof(Mission).GetProperty("Pointer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static bool _ready;
        private static bool _supported;
        private static int _thread;
        private static int _bindings;
        public static bool Ready => _ready && Thread.CurrentThread.ManagedThreadId == _thread;

        // Match the complete actual loaded module, not a guessed Steam path.
        public static void Initialize()
        {
            _ready = false;
            _supported = false;
            try
            {
                if (AgentPointer == null || MissionPointer == null) return;
                using (Process process = Process.GetCurrentProcess())
                foreach (ProcessModule module in process.Modules)
                {
                    if (!string.Equals(module.ModuleName, "TaleWorlds.Native.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string digest;
                    using (var hash = SHA256.Create())
                    using (var stream = File.OpenRead(module.FileName))
                        digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (digest != EngineHash)
                    {
                        PhysicsLog.Info("Living ragdoll native bridge refused: unsupported engine SHA-256.");
                        return;
                    }
                    _ready = bvr_ragdoll_prepare() == 0;
                    _supported = _ready;
                    _thread = Thread.CurrentThread.ManagedThreadId;
                    PhysicsLog.Info("Living ragdoll native bridge identity matched; ready=" + _ready + "; recovery still requires in-game validation.");
                    return;
                }
            }
            catch (Exception ex) { PhysicsLog.Error("Living ragdoll native bridge unavailable.", ex); }
        }

        // Bannerlord can move from its startup thread to a different mission
        // thread. Establish ownership at the actual serialized mission callback,
        // never from a render hook or a caller of Release itself.
        public static void BindMissionTick(Mission mission)
        {
            if (!_supported || mission != Mission.Current || mission.MissionEnded || GameNetwork.IsMultiplayer) return;
            int current = Thread.CurrentThread.ManagedThreadId;
            if (current == _thread) return;
            _ready = false;
            _ready = bvr_ragdoll_prepare() == 0;
            if (_bindings++ < 4)
                PhysicsLog.Info("Living ragdoll bridge mission-thread binding: startup/previous=" + _thread +
                    "; mission=" + current + "; ready=" + _ready);
            _thread = current;
            if (!_ready) _supported = false;
        }

        public static void Release(Agent agent, Mission mission, Skeleton skeleton)
        {
            if (!Ready || agent == null || !agent.IsActive() || skeleton == null ||
                agent.AgentVisuals?.GetSkeleton()?.Pointer != skeleton.Pointer)
                throw new InvalidOperationException("Living ragdoll release refused: ownership or thread changed.");
            UIntPtr agentPtr = (UIntPtr)AgentPointer.GetValue(agent);
            UIntPtr missionPtr = (UIntPtr)MissionPointer.GetValue(mission);
            int status = bvr_ragdoll_release(missionPtr, agentPtr, skeleton.Pointer);
            if (status != 0)
                throw new InvalidOperationException("Living ragdoll engine release refused: " + Failure(status));
        }

        public static bool Preflight(Agent agent, Mission mission, Skeleton skeleton)
        {
            if (!Ready || agent == null || !agent.IsActive() || skeleton == null ||
                agent.AgentVisuals?.GetSkeleton()?.Pointer != skeleton.Pointer) return false;
            int status = bvr_ragdoll_validate((UIntPtr)MissionPointer.GetValue(mission),
                (UIntPtr)AgentPointer.GetValue(agent), skeleton.Pointer);
            if (status != 0) PhysicsLog.Info("Living ragdoll preflight refused before activation: " + Failure(status));
            return status == 0;
        }

        public static void LogForceApi(Agent agent, Mission mission, Skeleton skeleton, sbyte bone)
        {
            if (!Ready) return;
            try
            {
                int status = bvr_ragdoll_force_api((UIntPtr)MissionPointer.GetValue(mission),
                    (UIntPtr)AgentPointer.GetValue(agent), skeleton.Pointer, bone, out UIntPtr vector, out UIntPtr scalar);
                PhysicsLog.Info("FORCE_API readonly status=" + status + "; vector_method_rva=0x" + vector.ToUInt64().ToString("x") +
                    "; scalar_method_rva=0x" + scalar.ToUInt64().ToString("x") + "; no_force_applied=True");
            }
            catch (Exception ex) { PhysicsLog.Error("Read-only ragdoll force-method inspection unavailable.", ex); }
        }

        public static bool ReadMass(Agent agent, Mission mission, Skeleton skeleton, out float mass)
        {
            mass = 0;
            if (!Ready || agent == null || !agent.IsActive() || skeleton == null ||
                agent.AgentVisuals?.GetSkeleton()?.Pointer != skeleton.Pointer) return false;
            int status = bvr_ragdoll_mass((UIntPtr)MissionPointer.GetValue(mission),
                (UIntPtr)AgentPointer.GetValue(agent), skeleton.Pointer, skeleton.GetBoneCount(), out mass, out int count);
            PhysicsLog.Info("BALANCE physical_mass status=" + status + "; bodies=" + count + "; total=" +
                mass.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            return status == 0 && InteractionMath.IsFinite(mass);
        }

        // Per-bone PhysX body masses: the mass of each body on the first bone
        // mapped to it, zero elsewhere. Same validated path as ReadMass.
        public static bool ReadBoneMasses(Agent agent, Mission mission, Skeleton skeleton, float[] boneMass)
        {
            if (!Ready || agent == null || !agent.IsActive() || skeleton == null || boneMass == null ||
                boneMass.Length < skeleton.GetBoneCount() ||
                agent.AgentVisuals?.GetSkeleton()?.Pointer != skeleton.Pointer) return false;
            int status = bvr_ragdoll_bone_masses((UIntPtr)MissionPointer.GetValue(mission),
                (UIntPtr)AgentPointer.GetValue(agent), skeleton.Pointer, skeleton.GetBoneCount(), boneMass,
                out float total, out int count);
            PhysicsLog.Info("ACTIVE_RAGDOLL bone_masses status=" + status + "; bodies=" + count + "; total=" +
                total.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            return status == 0 && InteractionMath.IsFinite(total);
        }

        // One angular acceleration per physical body (3 floats per bone, read
        // on each body's first bone). Stops for the mission on the first error.
        private static bool _torqueFailed;
        public static bool TorquesAvailable => Ready && !_torqueFailed;
        public static bool AddTorques(Agent agent, Mission mission, Skeleton skeleton, float[] torques)
        {
            if (!TorquesAvailable || agent == null || !agent.IsActive() || skeleton == null || torques == null ||
                torques.Length < skeleton.GetBoneCount() * 3 ||
                agent.AgentVisuals?.GetSkeleton()?.Pointer != skeleton.Pointer) return false;
            int status = bvr_ragdoll_add_torques((UIntPtr)MissionPointer.GetValue(mission),
                (UIntPtr)AgentPointer.GetValue(agent), skeleton.Pointer, skeleton.GetBoneCount(), torques, 3, out int applied);
            if (status != 0)
            {
                _torqueFailed = true;
                PhysicsLog.Info("ACTIVE_RAGDOLL rotation pins off: native torque status=" + status + " (" + Failure(status) + ").");
                return false;
            }
            if (!_torqueLogged) { _torqueLogged = true; PhysicsLog.Info("ACTIVE_RAGDOLL rotation pins on: torques applied to " + applied + " bodies."); }
            return true;
        }
        private static bool _torqueLogged;

        private static string Failure(int status)
        {
            switch (status)
            {
                case -1: return "bridge_thread_or_initialization";
                case -20: return "agent_memory";
                case -21: return "skeleton_memory";
                case -22: return "mission_fields_memory";
                case -23: return "native_agent_state";
                case -24: return "native_mission_identity";
                case -25: return "native_skeleton_body_identity";
                case -27: return "mission_ragdoll_list_bounds";
                case -28: return "ragdoll_not_active";
                default: return "native_status_" + status;
            }
        }

        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_ragdoll_prepare();
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_ragdoll_add_torques(UIntPtr mission, UIntPtr agent, UIntPtr skeleton,
            int boneCount, [In] float[] torques, int mode, out int applied);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_ragdoll_release(UIntPtr mission, UIntPtr agent, UIntPtr skeleton);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_ragdoll_validate(UIntPtr mission, UIntPtr agent, UIntPtr skeleton);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_ragdoll_force_api(UIntPtr mission, UIntPtr agent, UIntPtr skeleton,
            int bone, out UIntPtr vectorMethodRva, out UIntPtr scalarMethodRva);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_ragdoll_mass(UIntPtr mission, UIntPtr agent, UIntPtr skeleton,
            int boneCount, out float totalMass, out int bodyCount);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_ragdoll_bone_masses(UIntPtr mission, UIntPtr agent, UIntPtr skeleton,
            int boneCount, [Out] float[] boneMass, out float totalMass, out int bodyCount);
    }
}
