using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BannerlordVR.VR
{
    /// <summary>
    /// File logger. Bannerlord swallows most managed exceptions during module load
    /// and the in-game message feed disappears the moment a mission starts, so a
    /// file on disk is the only reliable way to see what happened. Written to
    /// Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.log.
    /// </summary>
    public static class VrLog
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static StreamWriter _writer;
        private static bool _failed;

        public static string Path => _path;

        public static void Initialize()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Logs");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "BannerlordVR.log");

                // ONE GENERATION IS KEPT. See the same change in bvr_log.cpp:
                // truncating per launch erased the record of a crash as soon as
                // the player did the obvious thing and relaunched, and the run
                // worth reading is nearly always the one before the one that
                // noticed. A full rolling log is still not worth it; the
                // previous run is.
                try
                {
                    string previous = System.IO.Path.Combine(dir, "BannerlordVR.prev.log");
                    if (File.Exists(_path))
                        File.Copy(_path, previous, overwrite: true);
                }
                catch
                {
                    // A locked or missing previous log is no reason to start the
                    // session without any logging at all.
                }

                // TEST 421: one writer for the session. File.AppendAllText opened,
                // appended and closed the file on the calling thread for every
                // line - up to ~60 a second in a battle. AutoFlush keeps the old
                // crash behaviour: each line is on disk when Write returns.
                _writer = new StreamWriter(
                    new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                    Encoding.UTF8) { AutoFlush = true };
                _writer.Write(
                    "=== BannerlordVR log opened " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    " ===" + Environment.NewLine);
            }
            catch
            {
                _failed = true;
            }
        }

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Error(string message, Exception ex) =>
            Write("ERROR", message + Environment.NewLine + ex);

        private static void Write(string level, string message)
        {
            if (_failed || _writer == null)
                return;

            try
            {
                lock (Gate)
                {
                    _writer.Write(
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                        " [" + level + "] " + message + Environment.NewLine);
                }
            }
            catch
            {
                _failed = true;
            }
        }
    }
}
