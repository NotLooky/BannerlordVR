using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR
{
    internal static class VrDlssRuntime
    {
        private const string RuntimeName = "nvngx_dlss.dll";
        private const string RuntimeHash = "3975567b8943c53acce397f2b72380092f84f162d00b0d2c7d08a1025c563983";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        internal static void Install()
        {
            try
            {
                string bundled = Path.Combine(Path.GetDirectoryName(typeof(VrDlssRuntime).Assembly.Location), RuntimeName);
                string installed = Path.Combine(Path.GetDirectoryName(typeof(MBSubModuleBase).Assembly.Location), RuntimeName);
                bool loaded = GetModuleHandle(RuntimeName) != IntPtr.Zero;
                if (Upgrade(bundled, installed) && loaded)
                    VrLog.Warn("DLSS was already loaded. Restart Bannerlord to use the bundled DLSS runtime.");
            }
            catch (Exception ex)
            {
                VrLog.Error("DLSS upgrade failed; continuing with the game's runtime. Close the game and copy "
                            + "the bundled nvngx_dlss.dll into the game bin folder manually if needed.", ex);
            }
        }

        internal static bool Upgrade(string bundled, string installed)
        {
            if (!File.Exists(bundled))
            {
                VrLog.Warn("Bundled DLSS runtime is missing; keeping the game's runtime.");
                return false;
            }
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(bundled))
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() != RuntimeHash)
                    throw new InvalidDataException("Bundled DLSS runtime failed its SHA-256 check.");

            Version version = VersionOf(bundled);
            bool exists = File.Exists(installed);
            if (exists && VersionOf(installed) >= version)
            {
                VrLog.Info("DLSS runtime is already equal or newer; keeping " + installed);
                return false;
            }

            string temporary = installed + ".bannerlordvr-" + Guid.NewGuid().ToString("N") + ".tmp";
            string backup = installed + ".bannerlordvr-original.bak";
            try
            {
                File.Copy(bundled, temporary);
                if (exists)
                    File.Replace(temporary, installed, File.Exists(backup) ? null : backup);
                else
                    File.Move(temporary, installed);
                VrLog.Info("Installed bundled DLSS " + version + " at " + installed + "; original backup: " + backup);
                return true;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static Version VersionOf(string path)
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (string.IsNullOrEmpty(info.FileVersion))
                throw new InvalidDataException("Cannot identify the existing DLSS version: " + path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
    }
}
