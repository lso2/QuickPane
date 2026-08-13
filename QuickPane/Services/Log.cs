using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace QuickPane.Services
{
    /// <summary>
    /// File logger and durable problem journal, writing under %APPDATA%\QuickPane\Logs and falling back
    /// to %TEMP% when that folder cannot be written.
    ///
    ///   debug.log    verbose trace of the current run and the two before it
    ///   events.log   low-volume journal of crashes, restarts, and glitches, kept across many sessions
    ///                so the record of when the app misbehaved can be read back weeks later
    ///
    /// Every write is best effort because logging must never be able to take the app down. The public
    /// surface is deliberately small: Info and Error for trace, Event for anything worth finding later,
    /// and Activity for the breadcrumb CrashGuard stamps into its heartbeat.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string _dir;
        private static string _debugPath;
        private static string _eventPath;
        private static string _sessionId = "--------";
        private static volatile string _activity = "starting";
        private static DateTime _activityAt = DateTime.Now;

        private const long DebugMaxBytes = 2L * 1024 * 1024;
        private const long EventMaxBytes = 512L * 1024;
        private const int DebugKeep = 2;
        private const int EventKeep = 8;

        /// <summary>Eight hex characters identifying this run, stamped on every line so interleaved
        /// sessions in the journal stay separable.</summary>
        public static string SessionId { get { return _sessionId; } }

        /// <summary>Folder holding the logs, for the tray menu's "Open log folder" item.</summary>
        public static string Folder { get { return _dir; } }

        public static string EventLogPath { get { return _eventPath; } }

        /// <summary>The most recent breadcrumb and when it was set, which CrashGuard writes into the
        /// heartbeat so a hard termination still names what the app was doing at the time.</summary>
        public static string LastActivity { get { return _activity; } }
        public static DateTime LastActivityAt { get { return _activityAt; } }

        public static void Init()
        {
            try
            {
                _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            }
            catch { }

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var root = Path.Combine(appData, "QuickPane");
                _dir = Path.Combine(root, "Logs");
                Directory.CreateDirectory(_dir);
                _debugPath = Path.Combine(_dir, "debug.log");
                _eventPath = Path.Combine(_dir, "events.log");
                MigrateLegacyLogs(root);
            }
            catch
            {
                _dir = Path.GetTempPath();
                _debugPath = Path.Combine(_dir, "quickpane.log");
                _eventPath = Path.Combine(_dir, "quickpane-events.log");
            }

            Roll(_debugPath, DebugMaxBytes, DebugKeep);
            Roll(_eventPath, EventMaxBytes, EventKeep);
        }

        /// <summary>Earlier versions wrote debug.log beside settings.json rather than in a Logs folder,
        /// so those files move once and keep the accumulated history reachable under the new layout.</summary>
        private static void MigrateLegacyLogs(string oldDir)
        {
            try
            {
                foreach (var name in new[] { "debug.log", "debug.log.1" })
                {
                    var from = Path.Combine(oldDir, name);
                    if (!File.Exists(from)) continue;
                    var to = Path.Combine(_dir, name.Replace("debug.log", "debug-previous.log"));
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
            }
            catch { }
        }

        public static void Info(string message) { Write(_debugPath, "INFO ", message); }

        public static void Error(string message) { Write(_debugPath, "ERROR", message); }

        public static void Error(string message, Exception ex)
        {
            Write(_debugPath, "ERROR", message + " :: " + Describe(ex));
        }

        /// <summary>Record something worth finding later. Categories in use are crash, restart, glitch,
        /// dialog, navigate, shortcut, and session; anything short and stable works.</summary>
        public static void Event(string category, string message)
        {
            var line = "[" + (category ?? "note") + "] " + message;
            Write(_eventPath, "EVENT", line);
            Write(_debugPath, "EVENT", line);
        }

        public static void Event(string category, string message, Exception ex)
        {
            Event(category, message + " :: " + Describe(ex));
        }

        /// <summary>Set the breadcrumb describing what the app is doing right now, which stays cheap
        /// enough for any hot path because it only assigns two fields.</summary>
        public static void Activity(string what)
        {
            if (string.IsNullOrEmpty(what)) return;
            _activity = what;
            _activityAt = DateTime.Now;
        }

        /// <summary>Flatten an exception and its inner chain into one readable block.</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "(no exception)";
            var sb = new System.Text.StringBuilder();
            var e = ex;
            int depth = 0;
            while (e != null && depth < 6)
            {
                if (depth > 0) sb.Append(Environment.NewLine).Append("  inner -> ");
                sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
                if (!string.IsNullOrEmpty(e.StackTrace)) sb.Append(Environment.NewLine).Append(e.StackTrace);
                e = e.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        public static string AppVersion()
        {
            try { return Assembly.GetExecutingAssembly().GetName().Version.ToString(); }
            catch { return "unknown"; }
        }

        public static string ExePath()
        {
            try
            {
                var p = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch { return null; }
        }

        public static void OpenFolder()
        {
            try
            {
                if (string.IsNullOrEmpty(_dir)) return;
                Process.Start(new ProcessStartInfo { FileName = _dir, UseShellExecute = true });
            }
            catch (Exception ex) { Error("open log folder", ex); }
        }

        private static void Write(string path, string level, string message)
        {
            if (path == null) path = Path.Combine(Path.GetTempPath(), "quickpane.log");
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] {" + _sessionId + "} " + message;
            try
            {
                lock (Gate) { File.AppendAllText(path, line + Environment.NewLine); }
            }
            catch
            {
                // Logging must never crash the app. If even the fallback path fails, give up silently.
            }
        }

        /// <summary>Rename path to path.1, shifting existing archives down and dropping the oldest, once
        /// the live file passes maxBytes. Several archives are kept so the journal covers weeks rather
        /// than only the current run.</summary>
        private static void Roll(string path, long maxBytes, int keep)
        {
            try
            {
                if (path == null) return;
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= maxBytes) return;

                var oldest = path + "." + keep;
                if (File.Exists(oldest)) File.Delete(oldest);
                for (int i = keep - 1; i >= 1; i--)
                {
                    var from = path + "." + i;
                    if (File.Exists(from)) File.Move(from, path + "." + (i + 1));
                }
                File.Move(path, path + ".1");
            }
            catch { }
        }
    }
}
