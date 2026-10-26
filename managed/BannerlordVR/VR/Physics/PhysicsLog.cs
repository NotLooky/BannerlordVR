using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace BannerlordVR.VR.Physics
{
    /// <summary>
    /// File logger. Bannerlord swallows most managed exceptions during module load
    /// and the in-game message feed disappears the moment a mission starts, so a
    /// file on disk is the only reliable way to see what happened. Written to
    /// Documents\Mount and Blade II Bannerlord\Logs\BannerlordVR.log.
    /// </summary>
    public static class PhysicsLog
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _failed;
        // Opening and closing the file per line cost milliseconds on the
        // mission thread (an 18.5 ms spike when three bodies activated at
        // once). Info and Warn lines are queued for a background writer; an
        // Error drains the queue synchronously so a crash keeps its context.
        private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent Signal = new AutoResetEvent(false);
        private static Thread _writer;

        public static string Path => _path;

        public static void Initialize()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Logs");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "BannerlordVR.Physics.log");

                // ONE GENERATION IS KEPT. See the same change in bvr_log.cpp:
                // truncating per launch erased the record of a crash as soon as
                // the player did the obvious thing and relaunched, and the run
                // worth reading is nearly always the one before the one that
                // noticed. A full rolling log is still not worth it; the
                // previous run is.
                try
                {
                    string previous = System.IO.Path.Combine(dir, "BannerlordVR.Physics.prev.log");
                    if (File.Exists(_path))
                        File.Copy(_path, previous, overwrite: true);
                }
                catch
                {
                    // A locked or missing previous log is no reason to start the
                    // session without any logging at all.
                }

                File.WriteAllText(_path,
                    "=== BannerlordVR.Physics log opened " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    " ===" + Environment.NewLine, Encoding.UTF8);
                if (_writer == null)
                {
                    _writer = new Thread(WriterLoop) { IsBackground = true, Name = "BannerlordVR.Physics.log" };
                    _writer.Start();
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush();
                }
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
            if (_failed || _path == null)
                return;
            Pending.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                " [" + level + "] " + message + Environment.NewLine);
            if (level == "ERROR" || _writer == null) Flush();
            else Signal.Set();
        }

        private static void WriterLoop()
        {
            while (!_failed)
            {
                Signal.WaitOne(250);
                Flush();
            }
        }

        public static void Flush()
        {
            if (_failed || _path == null || Pending.IsEmpty)
                return;
            try
            {
                lock (Gate)
                {
                    var text = new StringBuilder();
                    while (Pending.TryDequeue(out string line)) text.Append(line);
                    if (text.Length > 0) File.AppendAllText(_path, text.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                _failed = true;
            }
        }
    }
}
